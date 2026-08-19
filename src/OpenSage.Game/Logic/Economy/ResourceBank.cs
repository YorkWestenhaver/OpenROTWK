// The deterministic player resource ledger - GPL Money's deposit/withdraw semantics
// (generals-gpl GeneralsMD Common/RTS/Money.cpp: Money::withdraw / Money::deposit /
// Money::xfer; semantics only, fresh code) plus the BFME2 command-point pool (no GPL
// reference - INI-facts only, see the Ghidra-gap finding in
// research/systems/economy-production.md).
//
// DESIGN (the surface future economy/production modules and SimPlayer call):
//   - Money is an unsigned 32-bit balance, exactly the original (F3: money is int-family,
//     never Fix64). Withdraw CLAMPS to the balance and returns what was actually taken
//     (GPL Money::withdraw) - the affordability CHECK is the caller's job (GPL
//     BuildAssistant/UpgradeCenter shape); the ledger itself can never go negative.
//   - Audio hooks (money deposit/withdraw sounds) are the client's concern and do not
//     exist here; the caller raises ISimEvents if it wants feedback.
//   - CommandPointsBank is the BFME2 population pool: a limit set from game data
//     (GameData GoodCommandPointLimit / EvilCommandPointsMP* tables) and a used-count
//     driven by unit CommandPoints values. No GPL reference exists; the accounting here
//     is the minimal deterministic core (use/release, clamped at zero, afford test) and
//     is flagged as a behavioral gap until a game.dat spec pins the original's edge
//     cases.
//
// All state is Xfer-visible; declaration order is ours (F9).

using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Economy;

/// <summary>
/// The player's money ledger, GPL <c>Money</c>. Unsigned balance; withdraw clamps.
/// </summary>
[SimState]
public sealed class ResourceBank
{
    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----
    private uint _money;

    public ResourceBank(uint startingMoney = 0)
    {
        _money = startingMoney;
    }

    public uint Money => _money;

    /// <summary>
    /// GPL <c>Money::withdraw</c>: takes up to <paramref name="amountToWithdraw"/>,
    /// clamped to the balance, and returns the amount actually withdrawn.
    /// </summary>
    public uint Withdraw(uint amountToWithdraw)
    {
        if (amountToWithdraw > _money)
        {
            amountToWithdraw = _money;
        }

        if (amountToWithdraw == 0)
        {
            return 0;
        }

        _money -= amountToWithdraw;
        return amountToWithdraw;
    }

    /// <summary>GPL <c>Money::deposit</c>: zero deposits are a no-op.</summary>
    public void Deposit(uint amountToDeposit)
    {
        if (amountToDeposit == 0)
        {
            return;
        }

        _money += amountToDeposit;
    }

    /// <summary>Affordability test (GPL callers compare <c>countMoney() &gt;= cost</c>).</summary>
    public bool CanAfford(uint amount) => _money >= amount;

    /// <summary>Direct set - map/script initialization only (GPL <c>Money::init</c> shape).</summary>
    public void SetMoney(uint amount) => _money = amount;

    // ---- the single walk (save/load + CRC + deep-dump), F9 declaration order ----
    public void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferUInt("Money", ref _money, Tolerance.Quantum);
    }
}

/// <summary>
/// The BFME2 command-point (population) pool. No GPL reference; INI facts only
/// (unit <c>CommandPoints</c> costs against a per-player limit from GameData).
/// </summary>
[SimState]
public sealed class CommandPointsBank
{
    // ---- mutable sim state ----
    private int _limit;
    private int _used;

    public CommandPointsBank(int limit = 0)
    {
        _limit = limit;
        _used = 0;
    }

    public int Limit => _limit;

    public int Used => _used;

    public int Available => _limit - _used;

    /// <summary>
    /// Can <paramref name="commandPoints"/> more points be spent? Zero-cost entries are
    /// always allowed (structures and heroes with CommandPoints = 0).
    /// </summary>
    public bool CanAfford(int commandPoints) => commandPoints <= 0 || _used + commandPoints <= _limit;

    /// <summary>Claim points for a produced/spawned unit. Negative amounts are ignored.</summary>
    public void Use(int commandPoints)
    {
        if (commandPoints > 0)
        {
            _used += commandPoints;
        }
    }

    /// <summary>Return points when a unit dies or is refunded. Clamped at zero.</summary>
    public void Release(int commandPoints)
    {
        if (commandPoints > 0)
        {
            _used -= commandPoints;
            if (_used < 0)
            {
                _used = 0;
            }
        }
    }

    /// <summary>The limit changes mid-game (fortress upgrades, map settings, WotR bonuses).</summary>
    public void SetLimit(int limit) => _limit = limit;

    // ---- the single walk, F9 declaration order ----
    public void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferInt("Limit", ref _limit, Tolerance.Quantum);
        xfer.XferInt("Used", ref _used, Tolerance.Quantum);
    }
}
