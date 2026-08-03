using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using ConnectorLib.JSON;
using HollowTwitch.Entities;
using HollowTwitch.Entities.Attributes;
using HollowTwitch.Precondition;
using UnityEngine;
using UnityEngine.SceneManagement;
using UObject = UnityEngine.Object;

namespace HollowTwitch
{
    public class CommandProcessor
    {
        private const char Seperator = ' ';

        internal List<Command> Commands { get; }

        private readonly Dictionary<Type, IArgumentParser> _parsers;

        private readonly MonoBehaviour _coroutineRunner;

        private string _currentScene = string.Empty;

        /// <summary>
        /// Sink for asynchronous effect updates (Paused/Resumed/Finished).
        /// Set by the active client. May be null (e.g. during tests) - updates are simply dropped.
        /// </summary>
        internal Action<SimpleJSONResponse> SendResponse { get; set; }

        /// <summary>Tracks a command instance that is currently executing.</summary>
        private class RunningEffect
        {
            public uint? RequestId;
            public bool Timed;
            public bool StopRequested;
            public bool Paused;
            public float RemainingSeconds;
        }

        // Keyed by lower-case command name. One instance per command at a time - a second request
        // for the same command while one is running gets Retry, which prevents two instances from
        // capturing/restoring each other's "original" values (e.g. gravity, run speed).
        private readonly Dictionary<string, RunningEffect> _running = new();

        public CommandProcessor()
        {
            Commands = new List<Command>();
            _parsers = new Dictionary<Type, IArgumentParser>();

            var go = new GameObject();

            UObject.DontDestroyOnLoad(go);

            _coroutineRunner = go.AddComponent<NonBouncer>();
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene arg0, LoadSceneMode arg1) => _currentScene = arg0.name;

        public void AddTypeParser<T>(T parser, Type t) where T : IArgumentParser
        {
            _parsers.Add(t, parser);
        }

        //this could probably go somewhere better
        private static readonly HashSet<string> ExcludedAreas = new(new[]
        {
            "Quit_To_Menu",
            "Opening_Sequence",
            "Menu_Title",
            "Knight_Pickup",
            "Room_shop",
            "Cinematic_Stag_travel",
            "Room_Charm_Shop",
            "Room_Mender_House",
            "Room_nailsmith",
            "End_Credits",
            "Room_ruinhouse",
            "Room_mapper"
        });

        public IEnumerable<EffectResponseMetadata> GetMetadata()
        {
            // Metadata requests can arrive while no save is loaded - report what we safely can.
            HeroController hc = HeroController.instance;

            if (hc != null && hc.playerData != null)
            {
                yield return EffectResponseMetadata.Success("health", hc.playerData.health);
                yield return EffectResponseMetadata.Success("mpCharge", hc.playerData.MPCharge);
                yield return EffectResponseMetadata.Success("mpReserve", hc.playerData.MPReserve);
            }

            yield return EffectResponseMetadata.Success("location", _currentScene);
        }

        public GameUpdate GetGameState()
        {
            try
            {
                // No save loaded (main menu, quitting to menu, very early startup).
                if (HeroController.instance == null || HeroController.instance.cState == null)
                    return new(GameState.WrongMode);

                if (ExcludedAreas.Contains(_currentScene))
                    return new((_currentScene?.StartsWith("Room") ?? false) ? GameState.SafeArea : GameState.WrongMode);

                if (HeroController.instance.cState.isPaused)
                    return new(GameState.Paused);

                if (HeroController.instance.cState.dead)
                    return new(GameState.BadPlayerState);

                if (HeroController.instance.cState.transitioning)
                    return new(GameState.Loading);

                return new(GameState.Ready);
            }
            catch (Exception e)
            {
                Logger.LogError(e);
                return new(GameState.Error);
            }
        }

        /// <summary>Null-safe check for "effects can run right now".</summary>
        internal bool IsGameReady()
        {
            try
            {
                HeroController hc = HeroController.instance;

                return hc != null
                       && hc.cState != null
                       && !hc.cState.isPaused
                       && !hc.cState.dead
                       && !hc.cState.transitioning
                       && !ExcludedAreas.Contains(_currentScene);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Requests that a running effect stop early. Matches by original request id or command code.
        /// The effect's coroutine unwinds through its remaining logic (restoring state) and reports Finished.
        /// </summary>
        public bool RequestStop(string code, uint id)
        {
            bool found = false;

            string name = code?.Replace('_', ' ').Split(Seperator).FirstOrDefault();

            foreach (KeyValuePair<string, RunningEffect> kvp in _running)
            {
                RunningEffect re = kvp.Value;

                if (re.RequestId == id || (name != null && kvp.Key.Equals(name, StringComparison.InvariantCultureIgnoreCase)))
                {
                    re.StopRequested = true;
                    found = true;
                }
            }

            return found;
        }

        public (EffectStatus, Command) Execute(string user, string command, long? duration, uint? requestId = null, bool ignoreChecks = false)
        {
            if (!IsGameReady()) return (EffectStatus.Retry, null);

            string[] pieces = command.Split(Seperator);

            List<Command> found = Commands
                                  .Where(x => x.Name.Equals(pieces[0], StringComparison.InvariantCultureIgnoreCase))
                                  .OrderByDescending(x => x.Priority)
                                  .ToList();

            // Unknown effect code - tell the client to stop trying instead of queueing forever.
            if (found.Count == 0)
            {
                Logger.LogError($"No command found for \"{command}\".");
                return (EffectStatus.Unavailable, null);
            }

            bool sawTransientFailure = false;

            foreach (Command c in found)
            {
                bool allGood = true;

                foreach (PreconditionAttribute p in c.Preconditions)
                {
                    if (p is CooldownAttribute cooldown)
                    {
                        if (duration.HasValue) cooldown.Cooldown = TimeSpan.FromMilliseconds(duration.Value + 5);
                        if (p.Check(user)) continue;
                        allGood = false;
                        sawTransientFailure = true;

                        Logger.Log
                        (
                            $"The coodown for command {c.Name} failed. "
                            + $"The cooldown has {cooldown.MaxUses - cooldown.Uses} and will reset in {cooldown.ResetTime - DateTimeOffset.Now}"
                        );
                    }
                    else
                    {
                        if (p.Check(user)) continue;
                        allGood = false;

                        // Mutexes clear when the conflicting effect ends; other preconditions
                        // (e.g. ability requirements) may also become true later, so retry either way.
                        sawTransientFailure = true;
                    }
                }

                allGood |= ignoreChecks;

                if (!allGood)
                    continue;

                // Don't allow a second instance of the same command to overlap the first -
                // overlapping instances corrupt each other's saved/restored state.
                if (_running.ContainsKey(c.Name.ToLowerInvariant()))
                {
                    sawTransientFailure = true;
                    continue;
                }

                IEnumerable<string> args = pieces.Skip(1);
                if (duration.HasValue) args = args.Append(duration.Value.ToString("D"));

                if (!BuildArguments(args, c, out object[] parsed))
                    continue;

                foreach (PreconditionAttribute precond in c.Preconditions)
                {
                    precond.Use();
                }

                try
                {
                    Logger.Log($"Built arguments for command {command}.");

                    var effect = new RunningEffect
                    {
                        RequestId = requestId,
                        Timed = duration.HasValue,
                        RemainingSeconds = (duration ?? 0) / 1000f
                    };

                    _running[c.Name.ToLowerInvariant()] = effect;

                    _coroutineRunner.StartCoroutine(RunCommand(c, parsed, effect));
                    return (EffectStatus.Success, c);
                }
                catch (Exception e)
                {
                    _running.Remove(c.Name.ToLowerInvariant());
                    Logger.Log(e.ToString());
                }
            }

            // Transient failures (cooldown, mutex, already running) resolve on their own - retry.
            // Anything else (bad arguments, unmet hard preconditions) won't - refund the viewer.
            return (sawTransientFailure ? EffectStatus.Retry : EffectStatus.Failure, null);
        }

        private static readonly FieldInfo WaitForSecondsField =
            typeof(WaitForSeconds).GetField("m_Seconds", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// Runs a command's coroutine on the main thread, with:
        /// - duration waits gated on the game being ready (timers pause while paused/dead/transitioning),
        /// - Paused/Resumed updates sent to the client for timed effects,
        /// - guaranteed precondition reset even if the command throws,
        /// - a Finished (or Failure) report when the effect ends.
        /// </summary>
        private IEnumerator RunCommand(Command c, object[] parsed, RunningEffect effect)
        {
            /*
             * We have to wait a frame in order to make Unity itself call
             * the MoveNext on the IEnumerator
             *
             * This forces it to run on the main thread, so Unity doesn't break.
             */
            yield return null;

            bool errored = false;
            IEnumerator body = null;

            try
            {
                if (c.MethodInfo.ReturnType == typeof(IEnumerator))
                    body = c.MethodInfo.Invoke(c.ClassInstance, parsed) as IEnumerator;
                else
                    c.MethodInfo.Invoke(c.ClassInstance, parsed);
            }
            catch (Exception e)
            {
                errored = true;
                Logger.LogError(e);
            }

            // Drive the command (and any enumerators it yields) manually instead of handing
            // nested enumerators to Unity - that way duration waits are intercepted at every
            // nesting level (e.g. commands that "yield return PlayerDataUtil.FakeSet(...)").
            var stack = new Stack<IEnumerator>();
            if (body != null) stack.Push(body);

            while (stack.Count > 0)
            {
                IEnumerator current_enumerator = stack.Peek();
                object current;

                try
                {
                    if (!current_enumerator.MoveNext())
                    {
                        stack.Pop();
                        continue;
                    }

                    current = current_enumerator.Current;
                }
                catch (Exception e)
                {
                    // The command blew up mid-run; make sure we still clean up below
                    // so mutexes/cooldowns don't stay held forever.
                    errored = true;
                    Logger.LogError(e);
                    break;
                }

                // Intercept plain duration waits so effect timers pause while the game can't
                // actually show the effect (pause menu, death, transitions, menus).
                // Note: WaitForSecondsRealtime IS an IEnumerator (CustomYieldInstruction),
                // so these cases must precede the generic IEnumerator case.
                switch (current)
                {
                    case WaitForSecondsRealtime realtime:
                    {
                        IEnumerator wait = GatedWait(realtime.waitTime, effect, unscaled: true);
                        while (wait.MoveNext()) yield return wait.Current;
                        break;
                    }
                    case WaitForSeconds scaled when WaitForSecondsField?.GetValue(scaled) is float seconds:
                    {
                        IEnumerator wait = GatedWait(seconds, effect, unscaled: false);
                        while (wait.MoveNext()) yield return wait.Current;
                        break;
                    }
                    case IEnumerator nested:
                        stack.Push(nested);
                        break;
                    default:
                        yield return current;
                        break;
                }
            }

            EndEffect(c, effect, errored);
        }

        /// <summary>
        /// Waits for the given number of seconds, only counting down while the game is ready.
        /// Sends Paused/Resumed updates (with time remaining) for timed effects, and exits
        /// early if a stop was requested.
        /// </summary>
        private IEnumerator GatedWait(float seconds, RunningEffect effect, bool unscaled)
        {
            float remaining = seconds;

            while (remaining > 0f)
            {
                if (effect.StopRequested)
                    yield break;

                bool ready = IsGameReady();

                if (ready && effect.Paused)
                {
                    effect.Paused = false;
                    SendEffectUpdate(effect, EffectStatus.Resumed, remaining);
                }
                else if (!ready && !effect.Paused)
                {
                    effect.Paused = true;
                    SendEffectUpdate(effect, EffectStatus.Paused, remaining);
                }

                if (!effect.Paused)
                    remaining -= unscaled ? Time.unscaledDeltaTime : Time.deltaTime;

                effect.RemainingSeconds = remaining;

                yield return null;
            }
        }

        private void EndEffect(Command c, RunningEffect effect, bool errored)
        {
            _running.Remove(c.Name.ToLowerInvariant());

            foreach (PreconditionAttribute precond in c.Preconditions)
            {
                try { precond.Reset(); }
                catch (Exception e) { Logger.LogError(e); }
            }

            if (effect.RequestId == null)
                return;

            if (errored)
                SendEffectUpdate(effect, EffectStatus.Failure, 0);
            else if (effect.Timed)
                SendEffectUpdate(effect, EffectStatus.Finished, 0);
        }

        private void SendEffectUpdate(RunningEffect effect, EffectStatus status, float remainingSeconds)
        {
            if (effect.RequestId == null || !effect.Timed && status != EffectStatus.Failure)
                return;

            try
            {
                SendResponse?.Invoke(new EffectResponse
                {
                    id = effect.RequestId.Value,
                    status = status,
                    timeRemaining = (long)(remainingSeconds * 1000f)
                });
            }
            catch (Exception e)
            {
                Logger.LogError(e);
            }
        }

        private bool BuildArguments(IEnumerable<string> args, Command command, out object[] parsed)
        {
            parsed = null;

            // Avoid multiple enumerations when indexing
            string[] enumerated = args.ToArray();

            ParameterInfo[] parameters = command.Parameters;

            bool hasRemainder = parameters.Length != 0 && parameters[parameters.Length - 1].GetCustomAttributes(typeof(RemainingTextAttribute), false).Any();

            if (enumerated.Length < parameters.Length && !hasRemainder)
                return false;

            var built = new List<object>();

            for (int i = 0; i < parameters.Length; i++)
            {
                string toParse = enumerated[i];
                if (i == parameters.Length - 1)
                {
                    if (hasRemainder)
                    {
                        toParse = string.Join(Seperator.ToString(), enumerated.Skip(i).Take(enumerated.Length).ToArray());
                    }
                }

                object p = ParseParameter(toParse, parameters[i].ParameterType);

                if (p is null)
                    return false;

                if (parameters[i].GetCustomAttributes(typeof(EnsureParameterAttribute), false).FirstOrDefault() is EnsureParameterAttribute epa)
                    p = epa.Ensure(p);

                built.Add(p);
            }

            parsed = built.ToArray();

            return true;
        }

        private object ParseParameter(string arg, Type type)
        {
            TypeConverter converter = TypeDescriptor.GetConverter(type);

            try
            {
                return converter.ConvertFromString(arg);
            }
            catch
            {
                try
                {
                    return _parsers[type].Parse(arg);
                }
                catch
                {
                    return null;
                }
            }
        }

        public void RegisterCommands<T>()
        {
            MethodInfo[] methods = typeof(T).GetMethods(BindingFlags.Public | BindingFlags.Instance);

            object instance = Activator.CreateInstance(typeof(T));

            foreach (MethodInfo method in methods)
            {
                HKCommandAttribute attr = method.GetCustomAttributes(typeof(HKCommandAttribute), false).OfType<HKCommandAttribute>().FirstOrDefault();
                CooldownAttribute cldwn = method.GetCustomAttributes(typeof(CooldownAttribute), false).OfType<CooldownAttribute>().FirstOrDefault();

                if (attr == null)
                    continue;

                Commands.Add(new Command(attr.Name, method, instance, cldwn?.Cooldown));

                Logger.Log($"Added command: {attr.Name}");
            }
        }
    }
}
