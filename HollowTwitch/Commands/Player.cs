using System;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using GlobalEnums;
using HollowTwitch.Entities.Attributes;
using HollowTwitch.ModHelpers;
using HollowTwitch.Precondition;
using HollowTwitch.Utils;
using HutongGames.PlayMaker;
using Modding;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using Vasi;
using Random = UnityEngine.Random;
using UObject = UnityEngine.Object;

namespace HollowTwitch.Commands
{
    public class Player
    {
        // Most (all) of the commands stolen from Chaos Mod by Seanpr
        private GameObject _maggot;

        public Player()
        {
            IEnumerator GetMaggotPrime()
            {
                const string hwurmpURL = "https://cdn.discordapp.com/attachments/410556297046523905/716824653280313364/hwurmpU.png";

                UnityWebRequest www = UnityWebRequestTexture.GetTexture(hwurmpURL);

                yield return www.SendWebRequest();

                Texture texture = DownloadHandlerTexture.GetContent(www);

                Sprite maggotPrime = Sprite.Create
                (
                    (Texture2D)texture,
                    new Rect(0, 0, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f)
                );

                _maggot = new GameObject("maggot");
                _maggot.AddComponent<SpriteRenderer>().sprite = maggotPrime;
                _maggot.SetActive(false);

                UObject.DontDestroyOnLoad(_maggot);
            }

            GameManager.instance.StartCoroutine(GetMaggotPrime());
        }

        [HKCommand("ax2uBlind")]
        [Summary("Makes all rooms dark like lanternless rooms for a time.")]
        [Cooldown(20)]
        public IEnumerator Blind(long duration = 15000)
        {
            void OnSceneLoad(On.GameManager.orig_EnterHero orig, GameManager self, bool additiveGateSearch)
            {
                orig(self, additiveGateSearch);

                DarknessHelper.Darken();
            }

            DarknessHelper.Darken();
            On.GameManager.EnterHero += OnSceneLoad;

            yield return new WaitForSecondsRealtime(duration / 1000f);

            On.GameManager.EnterHero -= OnSceneLoad;
            DarknessHelper.Lighten();
        }

        [HKCommand("nopogo")]
        [Summary("Disables pogo knockback temporarily.")]
        [Cooldown(35)]
        public IEnumerator PogoKnockback()
        {
            void NoBounce(On.HeroController.orig_Bounce orig, HeroController self) { }

            On.HeroController.Bounce += NoBounce;

            yield return new WaitForSecondsRealtime(30);

            On.HeroController.Bounce -= NoBounce;
        }

        [HKCommand("conveyor")]
        [Summary("Floors or walls will act like conveyors")]
        [Cooldown(35)]
        [Mutex("BoundaryLimit")]
        public IEnumerator Conveyor(long duration = 30000)
        {
            // Horizontal floor conveyor only - the game's vertical conveyor component only
            // acts during wall-slides, so it read as the effect doing nothing.
            // Modest speed range, randomly left or right.
            float speed = Random.Range(4f, 12f) * (Random.Range(0, 2) == 0 ? 1f : -1f);

            HeroController hc = HeroController.instance;

            IEnumerator i = BoundaryLimit(() =>
            {
                hc.cState.onConveyor = true;
                hc.SetConveyorSpeed(speed);
            }, () =>
            {
                hc.cState.onConveyor = false;

                // conveyorSpeed persists on the HeroController and is shared with wind
                // zones/the wind effect - a stale value here hurls the player later.
                hc.SetConveyorSpeed(0f);
            }, duration / 1000f);

            while (i.MoveNext()) yield return i.Current;
        }

        [HKCommand("jumplength")]
        [Summary("Gives a random jump length.")]
        [Cooldown(35)]
        public IEnumerator JumpLength(long duration = 30000)
        {
            HeroController hc = HeroController.instance;

            int prev_steps = hc.JUMP_STEPS;

            hc.JUMP_STEPS = Random.Range(hc.JUMP_STEPS / 2, hc.JUMP_STEPS * 8);

            yield return new WaitForSecondsRealtime(duration / 1000f);

            hc.JUMP_STEPS = prev_steps;
        }

        [HKCommand("lifeblood")]
        [Cooldown(10)]
        public IEnumerator Lifeblood()
        {
            int r = Random.Range(1, 10);

            for (int i = 0; i < r; i++)
            {
                yield return null;

                EventRegister.SendEvent("ADD BLUE HEALTH");

                yield return null;
            }
        }

        [HKCommand("godmode")]
        [Cooldown(20)]
        public IEnumerator Godmode(long duration = 15000)
        {
            static int TakeHealth(int damage) => 0;

            static HitInstance HitInstance(Fsm owner, HitInstance hit)
            {
                hit.DamageDealt = 1 << 8;

                return hit;
            }

            ModHooks.TakeHealthHook += TakeHealth;
            ModHooks.HitInstanceHook += HitInstance;

            yield return new WaitForSecondsRealtime(duration / 1000f);

            ModHooks.TakeHealthHook -= TakeHealth;
            ModHooks.HitInstanceHook -= HitInstance;
        }

        public class GroundedConditionAttribute : PreconditionAttribute
        {
            public override bool Check(string user)
            {
                HeroController hc = HeroController.instance;

                return hc != null
                       && hc.cState != null
                       && hc.cState.onGround
                       && !hc.cState.recoiling
                       && !hc.cState.hazardDeath
                       && hc.transitionState == HeroTransitionState.WAITING_TO_TRANSITION;
            }
        }

        [HKCommand("sleep")]
        [GroundedCondition]
        [Cooldown(10)]
        public IEnumerator Sleep()
        {
            const string SLEEP_CLIP = "Wake Up Ground";

            HeroController hc = HeroController.instance;

            var anim = hc.GetComponent<HeroAnimationController>();

            float clipDuration = anim.GetClipDuration(SLEEP_CLIP);

            // If the clip can't be resolved the knight would "sleep" for 0 seconds while
            // still juggling control - just skip the effect entirely.
            if (clipDuration <= 0)
                yield break;

            bool cancelled = false;

            // Taking damage knocks the knight out of the sleep animation - end the effect
            // instead of leaving them stuck without control.
            int OnDamage(ref int hazardType, int damage)
            {
                if (damage > 0) cancelled = true;
                return damage;
            }

            ModHooks.TakeDamageHook += OnDamage;

            anim.PlayClip(SLEEP_CLIP);

            hc.StopAnimationControl();
            hc.RelinquishControl();

            try
            {
                for (float elapsed = 0; elapsed < clipDuration && !cancelled;)
                {
                    if (hc.cState.dead || hc.cState.hazardDeath || hc.cState.transitioning)
                        break;

                    // Scaled time so the sleep animation and timer stay in sync
                    // (both freeze while the game is paused).
                    elapsed += Time.deltaTime;

                    yield return null;
                }
            }
            finally
            {
                // Always wake back up, no matter how the sleep ended.
                ModHooks.TakeDamageHook -= OnDamage;

                hc.StartAnimationControl();
                hc.RegainControl();
            }
        }

        [HKCommand("limitSoul")]
        [Cooldown(35)]
        public IEnumerator LimitSoul()
        {
            // soulLimited is false in normal play - faking it to false did nothing.
            // Forcing true applies the Godhome-style soul cap for the duration.
            yield return PlayerDataUtil.FakeSet(nameof(PlayerData.soulLimited), true, 30);
        }

        [HKCommand("jumpspeed")]
        [Summary("Gives a random jump speed.")]
        [Cooldown(35)]
        public IEnumerator JumpSpeed(long duration = 30000)
        {
            HeroController hc = HeroController.instance;

            float prev_speed = hc.JUMP_SPEED;

            hc.JUMP_SPEED = Random.Range(hc.JUMP_SPEED / 4f, hc.JUMP_SPEED * 4f);

            yield return new WaitForSecondsRealtime(duration / 1000f);

            hc.JUMP_SPEED = prev_speed;
        }

        [HKCommand("printstate")]
        public IEnumerator PrintState()
        {
            string json = JsonConvert.SerializeObject(HeroController.instance.cState);
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            File.WriteAllText(desktop + "\\HKState.json", json);
            yield return null;
        }

        public IEnumerator BoundaryLimit(Action set, Action unset, float duration)
        {
            set();
            bool running = true;

            void BeforePlayerDead()
            {
                unset();
                running = false;
            }

            string BeforeSceneLoad(string arg)
            {
                BeforePlayerDead();
                return arg;
            }

            int BlueHealth()
            {
                BeforePlayerDead();
                return 0;
            }

            void CharmUpdate(PlayerData data, HeroController controller)
            {
                BeforePlayerDead();
            }

            ModHooks.BeforePlayerDeadHook += BeforePlayerDead;
            ModHooks.BeforeSceneLoadHook += BeforeSceneLoad;
            ModHooks.BlueHealthHook += BlueHealth;
            ModHooks.CharmUpdateHook += CharmUpdate;

            for (float elapsed = 0; elapsed < duration;)
            {
                bool ready = !(HeroController.instance.cState.isPaused
                               || HeroController.instance.cState.dead
                               || HeroController.instance.cState.hazardDeath
                               || HeroController.instance.cState.transitioning
                               || HeroController.instance.cState.nearBench);

                if (running)
                {
                    if (ready) elapsed += Time.unscaledDeltaTime;
                    else BeforePlayerDead();
                }
                else if (ready)
                {
                    set();
                    running = true;
                }
                yield return null;
            }

            try { ModHooks.BeforePlayerDeadHook -= BeforePlayerDead; } catch {/**/}
            try { ModHooks.BeforeSceneLoadHook -= BeforeSceneLoad; } catch {/**/}
            try { ModHooks.BlueHealthHook -= BlueHealth; } catch {/**/}
            try { ModHooks.CharmUpdateHook -= CharmUpdate; } catch {/**/}

            unset();
        }

        [HKCommand("wind")]
        [Summary("Make it a windy day.")]
        [Cooldown(35)]
        [Mutex("BoundaryLimit")]
        public IEnumerator Wind(long duration = 30000)
        {
            float speed = Random.Range(-6f, 6f);
            float prev_s = HeroController.instance.conveyorSpeed;

            IEnumerator i = BoundaryLimit(() =>
            {
                HeroController.instance.cState.inConveyorZone = true;
                HeroController.instance.conveyorSpeed = speed;
            }, () =>
            {
                HeroController.instance.cState.inConveyorZone = false;
                HeroController.instance.conveyorSpeed = prev_s;
            }, duration / 1000f);

            while (i.MoveNext()) yield return i.Current;
        }

        public class HasDashConditionAttribute : PreconditionAttribute
        {
            public override bool Check(string user) => PlayerData.instance.hasDash;
        }

        [HasDashCondition]
        [HKCommand("dashSpeed")]
        [Summary("Change dash speed.")]
        [Cooldown(35)]
        [Mutex("dash")]
        public IEnumerator DashSpeed(long duration = 30000)
        {
            HeroController hc = HeroController.instance;

            float len = Random.Range(.25f * hc.DASH_SPEED, hc.DASH_SPEED * 12f);
            float orig_dash = hc.DASH_SPEED;

            hc.DASH_SPEED = len;

            yield return new WaitForSecondsRealtime(duration / 1000f);

            hc.DASH_SPEED = orig_dash;
        }

        [HasDashCondition]
        [HKCommand("dashLength")]
        [Summary("Changes dash length.")]
        [Cooldown(35)]
        [Mutex("dash")]
        public IEnumerator DashLength(long duration = 30000)
        {
            HeroController hc = HeroController.instance;

            float len = Random.Range(.25f * hc.DASH_TIME, hc.DASH_TIME * 12f);
            float orig_dash = hc.DASH_TIME;

            hc.DASH_TIME = len;

            yield return new WaitForSecondsRealtime(duration / 1000f);

            hc.DASH_TIME = orig_dash;
        }

        [HasDashCondition]
        [HKCommand("dashVector")]
        [Summary("Changes dash vector. New vector generated when dashing in a new direction.")]
        [Cooldown(35)]
        [Mutex("dash")]
        public IEnumerator DashVector(long duration = 30000)
        {
            Vector2? vec = null;
            Vector2? orig = null;

            Vector2 VectorHook(Vector2 change)
            {
                if (
                    orig == change
                    && vec is Vector2 v
                )
                    return v;

                const float factor = 4f;

                orig = change;

                float mag = change.magnitude;

                float x = factor * Random.Range(-mag, mag);
                float y = factor * Random.Range(-mag, mag);

                return (Vector2)(vec = new Vector2(x, y));
            }

            ModHooks.DashVectorHook += VectorHook;

            yield return new WaitForSecondsRealtime(duration / 1000f);

            ModHooks.DashVectorHook -= VectorHook;
        }

        [HKCommand("triplejump")]
        [Summary("Gives you triple jump. Wings is enabled for the duration of this command.")]
        [Cooldown(35)]
        public IEnumerator TripleJump(long duration = 30000)
        {
            bool triple_jump = false;

            void Triple(On.HeroController.orig_DoDoubleJump orig, HeroController self)
            {
                orig(self);

                if (!triple_jump)
                {
                    Mirror.SetField(self, "doubleJumped", false);

                    triple_jump = true;
                }
                else
                {
                    triple_jump = false;
                }
            }

            On.HeroController.DoDoubleJump += Triple;

            yield return PlayerDataUtil.FakeSet(nameof(PlayerData.hasDoubleJump), true, duration / 1000f);

            On.HeroController.DoDoubleJump -= Triple;
        }

        [HKCommand("overflow")]
        [Cooldown(10)]
        [Summary("Gain more soul than you're able to carry, allowing you to cast 6 spells with base vessels.")]
        public void OverflowSoul()
        {
            HeroController.instance.AddMPChargeSpa(99 * 2);

            PlayerData.instance.MPCharge += 99;
        }

        [HKCommand("timescale")]
        [Summary("Changes the timescale of the game for the time specified. Limit: [0.01, 2f]")]
        [Cooldown(65)]
        [SuppressMessage("ReSharper", "CompareOfFloatsByEqualityOperator")]
        public IEnumerator ChangeTimescale([EnsureFloat(0.01f, 2f)] float scale, long duration = 60000)
        {
            SanicHelper.TimeScale = scale;

            Time.timeScale = Time.timeScale == 0 ? 0 : scale;

            yield return new WaitForSecondsRealtime(duration / 1000f);

            Time.timeScale = Time.timeScale == 0 ? 0 : 1;

            SanicHelper.TimeScale = 1;
        }

        // The knight's normal gravity scale, used when a sane value can't be captured
        // (e.g. gravity was already zeroed by a transition when the effect started).
        private const float DefaultGravityScale = 0.79f;

        [HKCommand("gravity")]
        [Summary("Changes the gravity to the specified scale. Scale Limit: [0.2, 1.9]")]
        [Cooldown(35)]
        [Mutex("gravity")]
        public IEnumerator ChangeGravity([EnsureFloat(0.2f, 1.90f)] float scale, long duration = 30000)
        {
            var rigidBody = HeroController.instance.gameObject.GetComponent<Rigidbody2D>();

            float def = rigidBody.gravityScale;

            // Never capture 0 as the value to restore - that leaves the knight floating forever.
            if (def <= Mathf.Epsilon)
                def = DefaultGravityScale;

            rigidBody.gravityScale = scale;

            yield return new WaitForSecondsRealtime(duration / 1000f);

            rigidBody.gravityScale = def;
        }


        [HKCommand("invertcontrols")]
        [Summary("Inverts the move direction of the player.")]
        [Cooldown(65)]
        public IEnumerator InvertControls(long duration = 60000)
        {
            void Invert(On.HeroController.orig_Move orig, HeroController self, float dir)
            {
                if (HeroController.instance.transitionState != HeroTransitionState.WAITING_TO_TRANSITION)
                {
                    orig(self, dir);

                    return;
                }

                orig(self, -dir);
            }

            Vector2 InvertDash(Vector2 change)
            {
                return -change;
            }

            On.HeroController.Move += Invert;
            ModHooks.DashVectorHook += InvertDash;

            yield return new WaitForSecondsRealtime(duration / 1000f);

            On.HeroController.Move -= Invert;
            ModHooks.DashVectorHook -= InvertDash;
        }

        [HKCommand("slippery")]
        [Summary("Makes the floor have no friction at all. Lasts for 60 seconds.")]
        [Cooldown(65)]
        [Mutex("BoundaryLimit")]
        public IEnumerator Slippery(long duration = 60000)
        {
            float last_move_dir = 0;

            void Slip(On.HeroController.orig_Move orig, HeroController self, float move_direction)
            {
                if (HeroController.instance.transitionState != HeroTransitionState.WAITING_TO_TRANSITION)
                {
                    orig(self, move_direction);

                    return;
                }

                // ReSharper disable once CompareOfFloatsByEqualityOperator
                if (move_direction == 0f)
                {
                    move_direction = last_move_dir;
                }

                orig(self, move_direction);

                last_move_dir = move_direction;
            }

            IEnumerator i = BoundaryLimit(() => On.HeroController.Move += Slip,
                () => On.HeroController.Move -= Slip,
                duration / 1000f);

            while (i.MoveNext()) yield return i.Current;
        }

        [HKCommand("nailscale")]
        [Summary("Makes the nail huge or tiny. Scale limit: [.3, 5]")]
        [Cooldown(35)]
        public IEnumerator NailScale([EnsureFloat(.3f, 5f)] float nailScale, long duration = 30000)
        {
            void ChangeNailScale(On.NailSlash.orig_StartSlash orig, NailSlash self)
            {
                orig(self);

                self.transform.localScale *= nailScale;
            }

            On.NailSlash.StartSlash += ChangeNailScale;

            yield return new WaitForSecondsRealtime(duration / 1000f);

            On.NailSlash.StartSlash -= ChangeNailScale;
        }

        [HKCommand("bindings")]
        [Summary("Enables bindings.")]
        [Cooldown(35)]
        public IEnumerator EnableBindings(long duration = 30000)
        {
            BindingsHelper.AddDetours();

            On.BossSceneController.RestoreBindings += BindingsHelper.NoOp;
            On.GGCheckBoundSoul.OnEnter += BindingsHelper.CheckBoundSoulEnter;

            BindingsHelper.ShowIcons();

            yield return new WaitForSecondsRealtime(duration / 1000f);

            BindingsHelper.Unload();
        }

        [HKCommand("float")]
        [Cooldown(15)]
        [Summary("Gain float for 10s.")]
        [Mutex("BoundaryLimit")]
        [Mutex("gravity")]
        public IEnumerator Float(long duration = 10000)
        {
            float savedScale = 0f;

            // Let the game keep zeroing gravity (transitions, hazard respawns) but block
            // re-enables while floating. A blanket no-op here used to swallow the game's
            // own restore, and AffectedByGravity(true) then brought back a saved
            // prevGravityScale that could itself be 0 - leaving the knight floating forever.
            void BlockGravityRestore(On.HeroController.orig_AffectedByGravity orig, HeroController self, bool gravityapplies)
            {
                if (!gravityapplies) orig(self, false);
            }

            IEnumerator i = BoundaryLimit(() =>
            {
                var rb = HeroController.instance.GetComponent<Rigidbody2D>();

                if (rb != null && rb.gravityScale > Mathf.Epsilon)
                    savedScale = rb.gravityScale;

                HeroController.instance.AffectedByGravity(false);
                On.HeroController.AffectedByGravity += BlockGravityRestore;
            }, () =>
            {
                On.HeroController.AffectedByGravity -= BlockGravityRestore;
                HeroController.instance.AffectedByGravity(true);

                // If the game's saved value was 0/stale, force a sane gravity scale -
                // never leave the knight weightless after the effect ends.
                var rb = HeroController.instance.GetComponent<Rigidbody2D>();

                if (rb != null && rb.gravityScale <= Mathf.Epsilon)
                    rb.gravityScale = savedScale > Mathf.Epsilon ? savedScale : DefaultGravityScale;
            }, duration / 1000f);

            while (i.MoveNext()) yield return i.Current;
        }

        [HKCommand("Salubra")]
        [Cooldown(30)]
        [Summary("Gain salubra's blessing even when off a bench within a room.")]
        public void Salubra()
        {
            GameObject bg = GameObject.Find("Blessing Ghost");

            bg.LocateMyFSM("Blessing Control").SetState("Start Blessing");
        }

        [HKCommand("hwurmpU")]
        [Summary("Summons a Hwurmp.")]
        [Cooldown(35)]
        public IEnumerator EnableMaggotPrimeSkin(long duration = 30000)
        {
            GameObject go = UObject.Instantiate(_maggot, HeroController.instance.transform.position + new Vector3(0, 0, -1f), Quaternion.identity);

            go.transform.parent = HeroController.instance.transform;

            go.SetActive(true);

            var renderer = HeroController.instance.GetComponent<MeshRenderer>();
            renderer.enabled = false;

            yield return new WaitForSecondsRealtime(duration / 1000f);

            UObject.DestroyImmediate(go);

            renderer.enabled = true;
        }

        [HKCommand("walkspeed")]
        [Cooldown(25)]
        [Summary("Gain a random walk speed. Limit: [0.3, 10]")]
        [Mutex("BoundaryLimit")]
        public IEnumerator WalkSpeed([EnsureFloat(0.3f, 10f)] float speed, long duration = 20000)
        {
            float prev_speed = HeroController.instance.RUN_SPEED;

            IEnumerator i = BoundaryLimit(() => HeroController.instance.RUN_SPEED *= speed,
                () => HeroController.instance.RUN_SPEED = prev_speed,
                duration / 1000f);

            while (i.MoveNext()) yield return i.Current;
        }

        [HKCommand("geo")]
        [Cooldown(10)]
        [Summary("Explode with geo.")]
        public void Geo()
        {
            GameObject[] geos = Resources.FindObjectsOfTypeAll<GameObject>().Where(x => x.name.StartsWith("Geo")).ToArray();

            GameObject large = geos.First(x => x.name.Contains("Large"));
            GameObject medium = geos.First(x => x.name.Contains("Med"));
            GameObject small = geos.First(x => x.name.Contains("Small"));

            small.SetActive(true);
            medium.SetActive(true);
            large.SetActive(true);

            HeroController.instance.proxyFSM.SendEvent("HeroCtrl-HeroDamaged");
            HeroController.instance.StartCoroutine(
                (IEnumerator)typeof(HeroController)
                              .GetMethod("StartRecoil", BindingFlags.NonPublic | BindingFlags.Instance)
                              ?.Invoke(HeroController.instance, new object[] { CollisionSide.left, true, 0 })
            );

            SpawnGeo(Random.Range(200, 4000), small, medium, large);
        }

        [HKCommand("respawn")]
        [Cooldown(10)]
        [Summary("Hazard respawn")]
        public void HazardRespawn()
        {
            // Don't trigger during transitions or anything
            if (HeroController.instance.transitionState != HeroTransitionState.WAITING_TO_TRANSITION)
                return;

            HeroController.instance.StartCoroutine(HeroController.instance.HazardRespawn());
        }

        [HKCommand("knockback")]
        [Cooldown(10)]
        public void Knockback(string dirStr)
        {
            CollisionSide dir = dirStr switch
            {
                "left" => CollisionSide.left,
                "right" => CollisionSide.right,
                "up" => CollisionSide.top,
                "top" => CollisionSide.top,
                "down" => CollisionSide.bottom,
                "bottom" => CollisionSide.bottom,
                _ => CollisionSide.other
            };

            HeroController.instance.proxyFSM.SendEvent("HeroCtrl-HeroDamaged");
            HeroController.instance.StartCoroutine(
                (IEnumerator)typeof(HeroController)
                              .GetMethod("StartRecoil", BindingFlags.NonPublic | BindingFlags.Instance)
                              ?.Invoke(HeroController.instance, new object[] { dir, true, 0 })
            );
        }

        private static void SpawnGeo(int amount, GameObject smallGeoPrefab, GameObject mediumGeoPrefab, GameObject largeGeoPrefab)
        {
            if (amount <= 0) return;

            if (smallGeoPrefab == null || mediumGeoPrefab == null || largeGeoPrefab == null)
            {
                HeroController.instance.AddGeo(amount);

                return;
            }

            var random = new System.Random();

            int smallNum = random.Next(0, amount / 10);
            amount -= smallNum;

            int largeNum = random.Next(amount / (25 * 2), amount / 25 + 1);
            amount -= largeNum * 25;

            int medNum = amount / 5;
            amount -= medNum * 5;

            smallNum += amount;

            FlingUtils.SpawnAndFling
            (
                new FlingUtils.Config
                {
                    Prefab = smallGeoPrefab,
                    AmountMin = smallNum,
                    AmountMax = smallNum,
                    SpeedMin = 15f,
                    SpeedMax = 30f,
                    AngleMin = 80f,
                    AngleMax = 115f
                },
                HeroController.instance.transform,
                new Vector3(0f, 0f, 0f)
            );

            FlingUtils.SpawnAndFling
            (
                new FlingUtils.Config
                {
                    Prefab = mediumGeoPrefab,
                    AmountMin = medNum,
                    AmountMax = medNum,
                    SpeedMin = 15f,
                    SpeedMax = 30f,
                    AngleMin = 80f,
                    AngleMax = 115f
                },
                HeroController.instance.transform,
                new Vector3(0f, 0f, 0f)
            );

            FlingUtils.SpawnAndFling
            (
                new FlingUtils.Config
                {
                    Prefab = largeGeoPrefab,
                    AmountMin = largeNum,
                    AmountMax = largeNum,
                    SpeedMin = 15f,
                    SpeedMax = 30f,
                    AngleMin = 80f,
                    AngleMax = 115f
                },
                HeroController.instance.transform,
                new Vector3(0f, 0f, 0f)
            );
        }

        [HKCommand("slaphand")]
        [Cooldown(65)]
        [Summary("Heavy blow if it was actually good.")]
        public IEnumerator SlapHand(long duration = 60000)
        {
            void SlashHit(Collider2D col, GameObject gameobject)
            {
                Vector3 dir = (col.transform.position - HeroController.instance.transform.position).normalized;

                if (!(col.gameObject.GetComponent<Rigidbody2D>() is Rigidbody2D rb2d)) return;

                rb2d.velocity = 40 * dir;
                rb2d.drag = 6;
            }

            ModHooks.SlashHitHook += SlashHit;

            yield return new WaitForSecondsRealtime(duration / 1000f);

            ModHooks.SlashHitHook -= SlashHit;
        }

        [HKCommand("toggleDash")]
        [Summary("Toggles dash for 45 seconds.")]
        [Cooldown(50)]
        [Mutex("dash")]
        public IEnumerator ToggleDash(long duration = 45000) => ToggleAbility("dash", duration);

        //don't use dash here, we want to mutex on the dash for that one
        [HKCommand("toggle")]
        [Summary("Toggles an ability for 45 seconds. Options: [superdash, claw, wings, nail, tear, dnail]")]
        [Cooldown(50)]
        public IEnumerator ToggleAbility(string ability, long duration = 45000)
        {
            float time = duration / 1000f;

            PlayerData pd = PlayerData.instance;

            switch (ability)
            {
                case "dash":
                    yield return PlayerDataUtil.FakeSet(nameof(PlayerData.canDash), pd.canDash ^ true, time);
                    break;
                case "superdash":
                    yield return PlayerDataUtil.FakeSet(nameof(PlayerData.hasSuperDash), pd.hasSuperDash ^ true, time);
                    break;
                case "claw":
                    yield return PlayerDataUtil.FakeSet(nameof(PlayerData.hasWalljump), pd.hasWalljump ^ true, time);
                    break;
                case "wings":
                    yield return PlayerDataUtil.FakeSet(nameof(PlayerData.hasDoubleJump), pd.hasDoubleJump ^ true, time);
                    break;
                case "tear":
                    yield return PlayerDataUtil.FakeSet(nameof(PlayerData.hasAcidArmour), pd.hasAcidArmour ^ true, time);
                    break;
                case "dnail":
                    yield return PlayerDataUtil.FakeSet(nameof(PlayerData.hasDreamNail), pd.hasDreamNail ^ true, time);
                    break;
                case "nail":
                    // attack_cooldown is in seconds; writing raw milliseconds locked the
                    // nail for ~12 hours instead of 45 seconds.
                    Mirror.SetField(HeroController.instance, "attack_cooldown", time);
                    break;
            }
        }

        [HKCommand("doubledamage")]
        [Summary("Makes the player take double damage.")]
        [Cooldown(35)]
        public IEnumerator DoubleDamage(long duration = 30000)
        {
            static int InstanceOnTakeDamageHook(ref int hazardtype, int damage) => damage * 2;
            ModHooks.TakeDamageHook += InstanceOnTakeDamageHook;
            yield return new WaitForSecondsRealtime(duration / 1000f);
            ModHooks.TakeDamageHook -= InstanceOnTakeDamageHook;
        }
    }
}