using Base.Utils.Maths;
using Harmony;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Common.Entities;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Entities.Weapons;
using PhoenixPoint.Tactical.Levels;
using PhoenixPointModLoader;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace pantolomin.phoenixPoint.mod.ppReturnFire
{
    public class Mod : IPhoenixPointMod
    {
        private const string FILE_NAME = "Mods/pp-return-fire-a-la-carte.properties";

        private const string ShotLimit = "ShotLimit";
        private static int shotLimit;
        private static Dictionary<TacticalActor, int> returnFireCounter = new Dictionary<TacticalActor, int>();

        private const string PerceptionRatio = "PerceptionRatio";
        private static float perceptionRatio;

        private const string AllowBashRiposte = "AllowBashRiposte";
        private static bool allowBashRiposte;

        private const string TargetCanRetaliate = "TargetCanRetaliate";
        private static bool targetCanRetaliate;

        private const string CasualtiesCanRetaliate = "CasualtiesCanRetaliate";
        private static bool casualtiesCanRetaliate;

        private const string BystandersCanRetaliate = "BystandersCanRetaliate";
        private static bool bystandersCanRetaliate;

        private const string CheckFriendlyFire = "CheckFriendlyFire";
        private static bool checkFriendlyFire;

        private const string ReactionAngle = "ReactionAngle";
        private static bool checkReactionAngle;
        private static float reactionAngleCos;

        private const string AllowReturnToCover = "AllowReturnToCover";
        private static bool allowReturnToCover;

        public ModLoadPriority Priority => ModLoadPriority.Low;

        public void Initialize()
        {
            Dictionary<string, string> rfProperties = new Dictionary<string, string>();
            try
            {
                foreach (string row in File.ReadAllLines(FILE_NAME))
                {
                    if (row.StartsWith("#")) continue;
                    string[] data = row.Split('=');
                    if (data.Length == 2)
                    {
                        rfProperties.Add(data[0].Trim(), data[1].Trim());
                    }
                }
            }
            catch (FileNotFoundException)
            {
                // simply ignore
            }
            catch (Exception e)
            {
                FileLog.Log(string.Concat("Failed to read the configuration file (", FILE_NAME, "):", e.ToString()));
            }

            shotLimit = getValue(rfProperties, ShotLimit, int.Parse, 1);
            if (shotLimit < 0)
            {
                FileLog.Log(string.Concat("Wrong limit for shots (", perceptionRatio, ") - should be positive or 0"));
                shotLimit = 1;
            }
            perceptionRatio = getValue(rfProperties, PerceptionRatio, float.Parse, 0.5f);
            if (perceptionRatio < 0f)
            {
                FileLog.Log(string.Concat("Wrong perception ratio provided (", perceptionRatio, ") - should be positive or 0"));
                perceptionRatio = 0.5f;
            }
            allowBashRiposte = getValue(rfProperties, AllowBashRiposte, bool.Parse, true);
            targetCanRetaliate = getValue(rfProperties, TargetCanRetaliate, bool.Parse, true);
            casualtiesCanRetaliate = getValue(rfProperties, CasualtiesCanRetaliate, bool.Parse, true);
            bystandersCanRetaliate = getValue(rfProperties, BystandersCanRetaliate, bool.Parse, false);
            checkFriendlyFire = getValue(rfProperties, CheckFriendlyFire, bool.Parse, true);
            int reactionAngle = getValue(rfProperties, ReactionAngle, int.Parse, 120);
            if (reactionAngle < 0 || reactionAngle > 360)
            {
                FileLog.Log(string.Concat("Wrong angle provided for return fire (", reactionAngle, ") - should be between 0 and 360"));
                reactionAngle = 90;
            }
            checkReactionAngle = reactionAngle < 360;
            reactionAngleCos = (float) Math.Cos(reactionAngle * Math.PI / 180d / 2d);
            allowReturnToCover = getValue(rfProperties, AllowReturnToCover, bool.Parse, true);

            HarmonyInstance harmonyInstance = HarmonyInstance.Create(typeof(Mod).Namespace);
            if (shotLimit > 0)
            {
                Patch(harmonyInstance, typeof(TacticalFaction), "PlayTurnCrt", null, "Pre_PlayTurnCrt");
                Patch(harmonyInstance, typeof(TacticalLevelController), "FireWeaponAtTargetCrt", null, "Pre_FireWeaponAtTargetCrt");
            }
            Patch(harmonyInstance, typeof(TacticalLevelController), "GetReturnFireAbilities", null, "Pre_GetReturnFireAbilities");
            if (allowReturnToCover)
            {
                // Transpiling to remove call to ReturnFire from ShootAndWaitRF ?
                // and add it after "STEP IN" in "FireWeaponAtTargetCrt"
            }
        }

        // ******************************************************************************************************************
        // ******************************************************************************************************************
        // Patched methods
        // ******************************************************************************************************************
        // ******************************************************************************************************************

        public static void Pre_PlayTurnCrt(TacticalFaction __instance)
        {
            // Keep in the map only the actors not belonging to the faction that is starting its turn
            returnFireCounter = returnFireCounter
                .Where(actor => actor.Key.TacticalFaction != __instance)
                .ToDictionary(actor => actor.Key, actor => actor.Value);
        }

        public static bool Pre_GetReturnFireAbilities(
            TacticalLevelController __instance,
            ref List<ReturnFireAbility> __result,
            TacticalActor shooter,
            Weapon weapon,
            TacticalAbilityTarget target,
            ShootAbility shootAbility, 
            bool getOnlyPossibleTargets = false, 
            List<TacticalActor> casualties = null)
        {
            // No return fire for the following attacks
            WeaponDef weaponDef = weapon?.WeaponDef;
            if (target.AttackType == AttackType.ReturnFire
                || target.AttackType == AttackType.Overwatch
                || target.AttackType == AttackType.Synced
                || target.AttackType == AttackType.ZoneControl
                || weaponDef != null && weaponDef.NoReturnFireFromTargets)
            {
                __result = null;
                return false;
            }

            List<ReturnFireAbility> list;
            IEnumerable<TacticalActor> actors = __instance.Map.GetActors<TacticalActor>(null);
            using (MultiForceDummyTargetableLock multiForceDummyTargetableLock = new MultiForceDummyTargetableLock(actors))
            {
                list = actors
                    // Get alive enemies for the shooter
                    .Where((TacticalActor actor) => {
                        return actor.IsAlive && actor.RelationTo(shooter) == FactionRelation.Enemy;
                    })
                    // Select the ones that have the return fire ability, ordered by priority
                    // Rmq: it is possible to have an actor twice if he has multiple RF abilities
                    .SelectMany((TacticalActor actor) =>
                        from a in actor.GetAbilities<ReturnFireAbility>()
                        orderby a.ReturnFireDef.ReturnFirePriority 
                        select a, (TacticalActor actor, ReturnFireAbility ability) => 
                            new { actor = actor, ability = ability }
                    )
                    // Check if shooter is a valid target for each actor/ability
                    .Where((actorAbilities) => 
                        actorAbilities.ability.IsEnabled(IgnoredAbilityDisabledStatesFilter.IgnoreNoValidTargetsFilter)
                            && actorAbilities.ability.IsValidTarget(shooter)
                    )
                    // Group by actor and keep only first valid ability
                    .GroupBy((actorAbilities) => actorAbilities.actor, (actorAbilities) => actorAbilities.ability)
                    .Select((IGrouping<TacticalActor, ReturnFireAbility> actorAbilities) => 
                        new { actorReturns = actorAbilities, actorAbility = actorAbilities.First() }
                    )
                    // Make sure the target of the attack is the first one to retaliate
                    .OrderByDescending((actorAbilities) => actorAbilities.actorAbility.TacticalActor == target.GetTargetActor())
                    .Select((actorAbilities) => actorAbilities.actorAbility)
                    .Where((ReturnFireAbility returnFireAbility) => {
                        TacticalActor tacticalActor = returnFireAbility.TacticalActor;
                        // Check that he has not retaliated too much already
                        if (shotLimit > 0)
                        {
                            returnFireCounter.TryGetValue(tacticalActor, out var currentCount);
                            if (currentCount >= shotLimit)
                            {
                                return false;
                            }
                        }
                        // Always allow bash riposte
                        if (returnFireAbility.ReturnFireDef.RiposteWithBashAbility)
                        {
                            return allowBashRiposte;
                        }
                        // Checks if the target is allowed to retaliate
                        // Rmq: Skipped when doing predictions on who will return fire (getOnlyPossibleTargets == false)
                        if (getOnlyPossibleTargets)
                        {
                            if (target.Actor == tacticalActor
                                || target.MultiAbilityTargets != null && target.MultiAbilityTargets.Any((TacticalAbilityTarget mat) => mat.Actor == tacticalActor))
                            {
                                // The actor was one of the targets
                                if (!targetCanRetaliate) return false;
                            } else if (casualties != null && casualties.Contains(tacticalActor))
                            {
                                // The actor was one of the casualties (not necessarily the target)
                                if (!casualtiesCanRetaliate) return false;
                            } else
                            {
                                if (!bystandersCanRetaliate) return false;
                            }
                        }
                        if (checkReactionAngle && !isAngleOK(shooter, tacticalActor))
                        {
                            return false;
                        }
                        // Check that target won't need to move to retaliate
                        ShootAbility defaultShootAbility = returnFireAbility.GetDefaultShootAbility();
                        TacticalAbilityTarget attackActorTarget = defaultShootAbility.GetAttackActorTarget(shooter, AttackType.ReturnFire);
                        if (attackActorTarget == null || !Utl.Equals(attackActorTarget.ShootFromPos, defaultShootAbility.Actor.Pos, 1E-05f))
                        {
                            return false;
                        }
                        TacticalActor tacticalActor1 = null;
                        // Prevent friendly fire
                        if (checkFriendlyFire && returnFireAbility.TacticalActor.TacticalPerception.CheckFriendlyFire(returnFireAbility.Weapon, attackActorTarget.ShootFromPos, attackActorTarget, out tacticalActor1, FactionRelation.Neutral | FactionRelation.Friend))
                        {
                            return false;
                        }
                        if (!returnFireAbility.TacticalActor.TacticalPerception.HasFloorSupportAt(returnFireAbility.TacticalActor.Pos))
                        {
                            return false;
                        }
                        // Check that we have a line of sight between both actors at a perception ratio (including stealth stuff)
                        if (!TacticalFactionVision.CheckVisibleLineBetweenActors(returnFireAbility.TacticalActor, returnFireAbility.TacticalActor.Pos, 
                            shooter, false, null, perceptionRatio))
                        {
                            return false;
                        }
                        return true;
                    }).ToList();
            }
            __result = list;
            return false;
        }

        public static void Pre_FireWeaponAtTargetCrt(Weapon weapon, TacticalAbilityTarget abilityTarget)
        {
            if (abilityTarget.AttackType == AttackType.ReturnFire)
            {
                TacticalActor tacticalActor = weapon.TacticalActor;
                returnFireCounter.TryGetValue(tacticalActor, out var currentCount);
                returnFireCounter[tacticalActor] = currentCount + 1;
            }
        }

        private static bool isAngleOK(TacticalActor shooter, TacticalActorBase target)
        {
            Vector3 targetForward = target.transform.TransformDirection(Vector3.forward);
            Vector3 targetToShooter = (shooter.Pos - target.Pos).normalized;
            float angleCos = Vector3.Dot(targetForward, targetToShooter);
            return Utl.GreaterThanOrEqualTo(angleCos, reactionAngleCos);
        }

        // ******************************************************************************************************************
        // ******************************************************************************************************************
        // Utility methods
        // ******************************************************************************************************************
        // ******************************************************************************************************************

        private void Patch(HarmonyInstance harmony, Type target, string toPatch, Type[] types, string prefix, string postfix = null)
        {
            MethodInfo original = types != null
                ? target.GetMethod(
                    toPatch,
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    types,
                    null)
                : target.GetMethod(toPatch, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            harmony.Patch(original, ToHarmonyMethod(prefix), ToHarmonyMethod(postfix), null);
        }

        private HarmonyMethod ToHarmonyMethod(string name)
        {
            if (name == null)
            {
                return null;
            }
            MethodInfo method = typeof(Mod).GetMethod(name);
            if (method == null)
            {
                throw new NullReferenceException(string.Concat("No method for name: ", name));
            }
            return new HarmonyMethod(method);
        }

        private object call(object instance, string methodName, params object[] parameters)
        {
            return call(instance, instance.GetType(), methodName, parameters);
        }
        private object call(object instance, Type type, string methodName, params object[] parameters)
        {
            MethodInfo method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                throw new NullReferenceException(string.Concat("No method for name \"", methodName, "\" in \"", instance.GetType(), "\""));
            }
            return method.Invoke(instance, parameters);
        }

        private object getField(object instance, string fieldName)
        {
            FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
            {
                foreach (FieldInfo f in instance.GetType().GetRuntimeFields())
                {
                    FileLog.Log(string.Concat("- field: ", f.Name));
                }
                throw new NullReferenceException(string.Concat("No field for name \"", fieldName, "\" in \"", instance.GetType(), "\""));
            }
            return field.GetValue(instance);
        }

        private object getProperty(object instance, string propertyName)
        {
            PropertyInfo property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (property == null)
            {
                foreach (PropertyInfo p in instance.GetType().GetRuntimeProperties())
                {
                    FileLog.Log(string.Concat("- property: ", p.Name));
                }
                throw new NullReferenceException(string.Concat("No property for name \"", propertyName, "\" in \"", instance.GetType(), "\""));
            }
            return property.GetValue(instance);
        }

        private T getValue<T>(Dictionary<string, string>  properties, string key, Func<string, T> mapper, T defaultValue)
        {
            string propertyValue;
            if (properties.TryGetValue(key, out propertyValue))
            {
                try
                {
                    return mapper.Invoke(propertyValue);
                }
                catch (Exception)
                {
                    FileLog.Log(string.Concat("Failed to parse value for key ", key, ": ", propertyValue));
                }
            }
            return defaultValue;
        }
    }
}
