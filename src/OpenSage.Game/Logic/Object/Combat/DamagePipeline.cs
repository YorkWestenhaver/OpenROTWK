// The firing -> damage delivery half of the S1 system: GPL
// WeaponTemplate::dealDamageInternal's victim selection and affects-mask filtering
// (generals-gpl GeneralsMD GameLogic/Object/Weapon.cpp:1221-1504, semantics only;
// fresh code), rebuilt on the frozen ISimContext seams.
//
// HOW A WEAPON/DAMAGE MODULE CALLS THIS (the public surface future ports use):
//   1. Own a SimWeapon for timing; when GetStatus(now) == ReadyToFire and the module's
//      own target/range logic says shoot, call SimWeapon.FireShot(...).
//   2. Build a CombatDamageInput (type/death/amount from the weapon's DamageNugget,
//      already Fix64 at parse).
//   3. Deliver:  DamagePipeline.DealDirectDamage(victim, input)          - point target
//                DamagePipeline.DealAreaDamage(ctx, source, victim, ...) - radius
//   4. The victim's Body resolves armor -> scalar -> health and returns the Fix64
//      CombatDamageOutput.
//
// Area-damage iteration goes through ISimContext.Partition (ascending ObjectId - the
// frozen deterministic order), so radius damage is never a desync source. NOTE (D-7):
// the underlying quadtree still measures distance in float; same-binary deterministic
// today, cross-arch when the partition system (S3) ports.
//
// GPL facts implemented (dealDamageInternal victim loop):
//   - the primary victim ignores ALL affects flags;
//   - KillsSelf turns the source's own entry into a HUGE_DAMAGE kill;
//   - without AffectsSelf, the source and anything it produced skip themselves;
//   - DoesntAffectSimilar skips allied same-template victims (terrorist domino guard);
//   - DoesntAffectAirborne skips significantly-airborne victims;
//   - otherwise relationship -> required Allies/Enemies/Neutrals flag.
// Not implemented here (recorded gaps): ZH primary/secondary radius split (BFME2
// nuggets carry one radius), BFME2 DamageTaperOff / MinRadius / CylinderAOE / DamageArc
// (no GPL reference - written behavioral spec needed), position-centered area damage (needs the
// Fix64 transform port; today's seam centers on an object).

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;

namespace OpenSage.Logic.Object;

/// <summary>
/// GPL WeaponAffectsMaskType (TheWeaponAffectsMaskNames): who radius damage may touch.
/// </summary>
[System.Flags]
public enum WeaponAffectsTypes
{
    None = 0,
    [IniEnum("SELF")]
    Self = 1 << 0,
    [IniEnum("ALLIES")]
    Allies = 1 << 1,
    [IniEnum("ENEMIES")]
    Enemies = 1 << 2,
    [IniEnum("NEUTRALS")]
    Neutrals = 1 << 3,
    [IniEnum("SUICIDE")]
    Suicide = 1 << 4,          // GPL WEAPON_KILLS_SELF
    [IniEnum("NOT_SIMILAR")]
    NotSimilar = 1 << 5,       // GPL WEAPON_DOESNT_AFFECT_SIMILAR
    [IniEnum("NOT_AIRBORNE")]
    NotAirborne = 1 << 6,      // GPL WEAPON_DOESNT_AFFECT_AIRBORNE
}

[SimState]
public static class DamagePipeline
{
    /// <summary>GPL HUGE_DAMAGE_AMOUNT (Damage.h:306): "always lethal".</summary>
    public static readonly Fix64 HugeDamageAmount = new Fix64(999999);

    /// <summary>
    /// Point-target delivery: the victim's Body resolves armor, scalar and health
    /// application and reports what actually happened.
    /// </summary>
    public static CombatDamageOutput DealDirectDamage(GameObject victim, in CombatDamageInput input)
    {
        if (victim == null || victim.IsDestroyed)
        {
            return new CombatDamageOutput { NoEffect = true };
        }

        return victim.AttemptCombatDamage(input);
    }

    /// <summary>
    /// Radius delivery centered on <paramref name="centerVictim"/> (GPL
    /// dealDamageInternal's iterateObjectsInRange loop). The primary victim - when
    /// among the results - bypasses every affects check, exactly like the original.
    /// Every victim receives the full input amount (BFME2 nugget = one radius/amount;
    /// taper-off is a recorded gap).
    /// </summary>
    public static void DealAreaDamage(
        ISimContext context,
        GameObject source,
        GameObject centerVictim,
        Fix64 radius,
        WeaponAffectsTypes affects,
        in CombatDamageInput input)
    {
        if (centerVictim == null)
        {
            return;
        }

        if (radius <= Fix64.Zero)
        {
            DealDirectDamage(centerVictim, input);
            return;
        }

        // The primary victim first: it bypasses every affects check (GPL: "anytime
        // something is designated as the primary victim, we ignore all the affects
        // flags"). It is damaged explicitly because the partition seam's FindNearby
        // excludes the query center from its own results.
        DealDirectDamage(centerVictim, input);

        foreach (var victim in context.Partition.QueryObjectsInRadius(centerVictim, radius))
        {
            if (victim.IsDestroyed || victim == centerVictim)
            {
                continue;
            }

            var killSelf = false;

            if (source != null && victim != centerVictim)
            {
                if ((affects & WeaponAffectsTypes.Suicide) != 0 && victim == source)
                {
                    killSelf = true;
                }
                else
                {
                    if ((affects & WeaponAffectsTypes.Self) == 0)
                    {
                        // The source never hurts itself - nor its own projectiles' launcher
                        // relationship: anything the source produced skips it too.
                        if (victim == source || source.CreatedByObjectID == victim.Id)
                        {
                            continue;
                        }
                    }

                    if ((affects & WeaponAffectsTypes.NotSimilar) != 0 &&
                        source.Definition == victim.Definition &&
                        GetRelationship(source, victim) == CombatRelationship.Allies)
                    {
                        continue;
                    }

                    if ((affects & WeaponAffectsTypes.NotAirborne) != 0 &&
                        context.Terrain.IsSignificantlyAboveTerrain(victim))
                    {
                        continue;
                    }

                    var required = GetRelationship(source, victim) switch
                    {
                        CombatRelationship.Allies => WeaponAffectsTypes.Allies,
                        CombatRelationship.Enemies => WeaponAffectsTypes.Enemies,
                        _ => WeaponAffectsTypes.Neutrals,
                    };

                    if ((affects & required) == 0)
                    {
                        continue;
                    }
                }
            }

            var victimInput = input;
            if (killSelf)
            {
                // GPL: blindly inflict a very high value of the intended damage type
                // (respects resist/death-type semantics better than reading health).
                victimInput.Amount = HugeDamageAmount;
            }

            victim.AttemptCombatDamage(victimInput);
        }
    }

    public enum CombatRelationship
    {
        Neutral,
        Allies,
        Enemies,
    }

    /// <summary>
    /// Relationship as the sim sees it (GPL getRelationship, reduced to the player
    /// alliance sets the fork exposes today): same owner or mutual-ally set = Allies;
    /// enemy set = Enemies; everything else Neutral.
    /// </summary>
    public static CombatRelationship GetRelationship(GameObject a, GameObject b)
    {
        if (a == null || b == null)
        {
            return CombatRelationship.Neutral;
        }

        if (a.Owner == b.Owner || a.Owner.Allies.Contains(b.Owner))
        {
            return CombatRelationship.Allies;
        }

        if (a.Owner.Enemies.Contains(b.Owner))
        {
            return CombatRelationship.Enemies;
        }

        return CombatRelationship.Neutral;
    }
}
