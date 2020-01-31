using Base.Utils.Maths;
using Harmony;
using PhoenixPoint.Common.Core;
using PhoenixPoint.Common.Entities;
using PhoenixPoint.Tactical.Entities;
using PhoenixPoint.Tactical.Entities.Abilities;
using PhoenixPoint.Tactical.Entities.Weapons;
using PhoenixPoint.Tactical.Levels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace pantolomin.phoenixPoint.mod.ppReturnFire
{
    public class Mod
    {
        private const string FILE_NAME = "Mods/pp-return-fire-a-la-carte.properties";

        private const string PerceptionRatio = "PerceptionRatio";
        private static float perceptionRatio;

        public static void Init()
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
            perceptionRatio = getValue(rfProperties, PerceptionRatio, float.Parse, 0.5f);
            HarmonyInstance harmonyInstance = HarmonyInstance.Create(typeof(Mod).Namespace);
            Mod.Patch(harmonyInstance, typeof(TacticalLevelController), "GetReturnFireAbilities", null, null, "Pre_GetReturnFireAbilities");
        }

        // ******************************************************************************************************************
        // ******************************************************************************************************************
        // Patched methods
        // ******************************************************************************************************************
        // ******************************************************************************************************************

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
                    .Where<TacticalActor>((TacticalActor actor) => {
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
                        new { actorReturns = actorAbilities, actorAbility = actorAbilities.First<ReturnFireAbility>() }
                    )
                    // Make sure the target of the attack is the first one to retaliate
                    .OrderByDescending((actorAbilities) => actorAbilities.actorAbility.TacticalActor == target.GetTargetActor())
                    .Select((actorAbilities) => actorAbilities.actorAbility)
                    .Where<ReturnFireAbility>((ReturnFireAbility returnFireAbility) => {
                        // Always allow bash riposte
                        if (returnFireAbility.ReturnFireDef.RiposteWithBashAbility)
                        {
                            return true;
                        }
                        // ?????????????????????
                        TacticalActor tacticalActor = returnFireAbility.TacticalActor;
                        if (getOnlyPossibleTargets 
                            && target.Actor != tacticalActor 
                            && (target.MultiAbilityTargets == null || !target.MultiAbilityTargets.Any<TacticalAbilityTarget>((TacticalAbilityTarget mat) => mat.Actor == tacticalActor)) && (casualties == null || !casualties.Contains(tacticalActor)))
                        {
                            return false;
                        }
                        ShootAbility defaultShootAbility = returnFireAbility.GetDefaultShootAbility();
                        TacticalAbilityTarget attackActorTarget = defaultShootAbility.GetAttackActorTarget(shooter, AttackType.ReturnFire);
                        if (attackActorTarget == null || !Utl.Equals(attackActorTarget.ShootFromPos, defaultShootAbility.Actor.Pos, 1E-05f))
                        {
                            return false;
                        }
                        TacticalActor tacticalActor1 = null;
                        // Prevent friendly fire
                        if (returnFireAbility.TacticalActor.TacticalPerception.CheckFriendlyFire(returnFireAbility.Weapon, attackActorTarget.ShootFromPos, attackActorTarget, out tacticalActor1, FactionRelation.Neutral | FactionRelation.Friend))
                        {
                            return false;
                        }
                        if (!returnFireAbility.TacticalActor.TacticalPerception.HasFloorSupportAt(returnFireAbility.TacticalActor.Pos))
                        {
                            return false;
                        }
                        // Check that we have a line of sight between both actors at half perception
                        if (!TacticalFactionVision.CheckVisibleLineBetweenActors(returnFireAbility.TacticalActor, returnFireAbility.TacticalActor.Pos, 
                            shooter, false, null, perceptionRatio))
                        {
                            return false;
                        }
                        return true;
                    }).ToList<ReturnFireAbility>();
            }
            __result = list;
            return false;
        }

        // ******************************************************************************************************************
        // ******************************************************************************************************************
        // Utility methods
        // ******************************************************************************************************************
        // ******************************************************************************************************************

        private static void Patch(HarmonyInstance harmony, Type target, string toPatch, Type[] types, string prefix, string postfix = null)
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

        private static HarmonyMethod ToHarmonyMethod(string name)
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

        private static object callPrivate(object instance, string methodName, params object[] parameters)
        {
            MethodInfo method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
            {
                throw new NullReferenceException(string.Concat("No method for name \"", methodName, "\" in \"", instance.GetType(), "\""));
            }
            return method.Invoke(instance, parameters);
        }

        private static object getField(object instance, string fieldName)
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

        private static object getProperty(object instance, string propertyName)
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

        private static T getValue<T>(Dictionary<string, string>  properties, string key, Func<string, T> mapper, T defaultValue)
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
