#nullable enable

using System;
using OpenSage.Data.Ini;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

/// <summary>
/// First death is intercepted and sets flags and max health.
/// Second death is handled normally.
/// </summary>
[AddedIn(SageGame.CncGeneralsZeroHour)]
public sealed class UndeadBody : ActiveBody
{
    private readonly UndeadBodyModuleData _moduleData;

    /// <summary>
    /// This is false until I detect death the first time, then I change my
    /// max, initial, and current health, and stop intercepting anything.
    /// </summary>
    private bool _isSecondLife;

    internal UndeadBody(GameObject gameObject, IGameEngine gameEngine, UndeadBodyModuleData moduleData)
        : base(gameObject, gameEngine, moduleData)
    {
        _moduleData = moduleData;
    }

    public override DamageInfoOutput AttemptDamage(in DamageInfoInput damageInput)
    {
        // If we are on our first life, see if this damage will kill us.
        // If it will, bind it to one hitpoint remaining, then go ahead
        // and take it.
        var shouldStartSecondLife = false;

        var modifiedDamageInput = damageInput;

        if (damageInput.DamageType != DamageType.Unresistable
            && !_isSecondLife
            && damageInput.Amount >= Health
            && damageInput.DamageType.IsHealthDamagingDamage())
        {
            modifiedDamageInput.Amount = Math.Min(damageInput.Amount, Health - 1);
            shouldStartSecondLife = true;
        }

        // R7 fix: route the (possibly clamped) hit to the *base* ActiveBody damage
        // resolution, not this overridden method. The prior port called the
        // unqualified AttemptDamage, which re-entered this method and recursed until
        // stack overflow (finding F-UB-1). GPL calls ActiveBody::attemptDamage here.
        var damageOutput = base.AttemptDamage(modifiedDamageInput);

        // After we take it (which allows for damaging special effects),
        // we will do our modifications to the body module.
        if (shouldStartSecondLife)
        {
            StartSecondLife(damageInput, damageOutput);
        }

        return damageOutput;
    }

    private void StartSecondLife(in DamageInfoInput damageInput, in DamageInfoOutput damageOutput)
    {
        // Flag module as no longer intercepting damage.
        _isSecondLife = true;

        // Modify ActiveBody's max health and initial health. SecondLifeMaxHealth is
        // audited to Fix64 (S5 vocabulary); it widens once here to the float SetMaxHealth
        // contract surface that ActiveBody still exposes (the D-7 health boundary S1
        // deferred). Integer literals - the entire corpus, default 1 - are bit-exact.
        SetMaxHealth(_moduleData.SecondLifeMaxHealth.ToFloatForDisplay(), MaxHealthChangeType.FullyHeal);

        // Set Armor set flag to use second life armor.
        SetArmorSetFlag(ArmorSetCondition.SecondLife);

        // Fire the Slow Death module. The fact that this is not the result of
        // OnDie will cause the special behavior.
        var total = 0;
        foreach (var module in GameObject.FindBehaviors<SlowDeathBehavior>())
        {
            if (module.IsDieApplicable(damageInput))
            {
                total += module.GetProbabilityModifier(damageOutput);
            }
        }
        DebugUtility.AssertCrash(total > 0, "Hmm, this is wrong");

        // This returns a value from 1...total, inclusive.
        var roll = GameEngine.GameLogic.Random.Next(1, total);

        foreach (var module in GameObject.FindBehaviors<SlowDeathBehavior>())
        {
            if (module.IsDieApplicable(damageInput))
            {
                roll -= module.GetProbabilityModifier(damageOutput);
                if (roll <= 0)
                {
                    module.BeginSlowDeath(damageInput);
                    return;
                }
            }
        }
    }

    // ---- contract Xfer (GPL UndeadBody::xfer, version 1): the second-life flag is sim
    // state and must fold into the Objects CRC channel alongside ActiveBody's Fix64
    // ledger. Declaration order = GPL order (F9): own version -> base walk -> the flag. ----

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        base.Xfer(xfer);                             // ActiveBody: version + Fix64 ledger + crush/indestructible
        xfer.XferBool("IsSecondLife", ref _isSecondLife); // Exact (A3): a boolean has no quantum gap
    }

    internal override void Load(StatePersister reader)
    {
        // Retail .sav layout mirrors GPL UndeadBody::xfer (F9-exempt legacy reader):
        // own version, then ActiveBody's persisted body, then the flag.
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        reader.PersistBoolean(ref _isSecondLife);
    }
}

/// <summary>
/// Treats the first death as a state change. Triggers the Use of SECOND_LIFE
/// ModelConditionState/ArmorSet and allows the use of the BattleBusSlowDeathBehavior module.
/// </summary>
[AddedIn(SageGame.CncGeneralsZeroHour)]
[SimDataAudited]
public sealed class UndeadBodyModuleData : ActiveBodyModuleData
{
    internal static new UndeadBodyModuleData Parse(IniParser parser)
    {
        var result = parser.ParseBlock(FieldParseTable);
        result.ApplyHealthDefaults(parser);   // F-HB-1: same shadowing-Parse defaulting bug as HighlanderBody.
        return result;
    }

    private static new readonly IniParseTable<UndeadBodyModuleData> FieldParseTable = ActiveBodyModuleData.FieldParseTable
        .Concat(new IniParseTable<UndeadBodyModuleData>
        {
            // Health quantity -> Fix64 at the S5 blessed integer text boundary (was ParseFloat).
            { "SecondLifeMaxHealth", (parser, x) => x.SecondLifeMaxHealth = parser.ParseFix64() },
        });

    public SimCore.Numerics.Fix64 SecondLifeMaxHealth { get; private set; } = SimCore.Numerics.Fix64.One;

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new UndeadBody(gameObject, gameEngine, this);
    }
}
