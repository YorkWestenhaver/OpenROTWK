// R15 S9-05: the ledger reconciliation between the two money implementations that exist in
// this engine today.
//
// THE PROBLEM THIS FILE SOLVES. There are two independent money ledgers:
//
//   * Logic/BankAccount.cs - `Player.BankAccount`. This is the LIVE ledger. It is what
//     Player.FromMapData seeds from PlayerTemplate.StartMoney + DefaultStartingCash, what
//     OrderProcessor's existing BuildObject/CreateUnit/BeginUpgrade/Sell cases charge and
//     refund, what Player.Persist walks for save/load, and what the UI reads.
//
//   * Logic/Economy/ResourceBank.cs - the S4 deterministic ledger (GPL Money semantics,
//     Xfer-visible) that the future SimPlayer will own. NOTHING constructs one per player
//     today: every ResourceBank instance in the tree is built by a test.
//
// CastleOrderHandler was written against the second one (its `CastleBankResolver` returned a
// ResourceBank), so wiring it into the order pipe as-written would have resolved every
// player to a bank nobody funds and nobody reads. The affordability guard would then be
// checked against a ledger that is not the player's money, and the withdraw would land in a
// ledger nothing spends from - i.e. castle/foundation construction, for the human AND for
// the S9 skirmish AI, would have been FREE. That is the "or the AI builds for free" failure
// this packet was told to resolve explicitly, and this file is the explicit resolution:
//
//   The live ledger is Player.BankAccount. Full stop. CastleBankResolver no longer names a
//   concrete ledger class; it returns this interface, GameLogic's production resolver
//   (PlayerFunds.ForPlayer) binds it to Player.BankAccount, and ResourceBank implements the
//   same interface so the existing S4-shaped tests keep constructing banks directly. When
//   SimPlayer lands and ResourceBank becomes the live ledger, the change is one line in
//   PlayerFunds.ForPlayer, not a rewrite of every caller.
//
// AUDIO: BankAccount plays a money sound on every deposit/withdraw by default. Order
// EXECUTION is sim-side and runs identically on every peer for every player, so it must not
// raise the local client's money sound - an AI player buying a barracks would otherwise beep
// at the human. BankAccountFunds therefore charges with playSound: false; feedback for the
// local player's own purchases belongs to the client/order-generator layer that raised the
// order. (ResourceBank's own header states the same rule from the other side: "Audio hooks
// ... are the client's concern and do not exist here".)

#nullable enable

namespace OpenSage.Logic.Economy;

/// <summary>
/// The minimal money-ledger surface an order handler needs: check, charge, refund. Both
/// <see cref="ResourceBank"/> (the S4/SimPlayer ledger) and <see cref="BankAccountFunds"/>
/// (the live <see cref="BankAccount"/>) implement it, so a handler can be written once
/// against whichever ledger the host binds.
/// </summary>
public interface IPlayerFunds
{
    /// <summary>The affordability CHECK. Callers test this before charging; the charge clamps.</summary>
    bool CanAfford(uint amount);

    /// <summary>Takes up to <paramref name="amount"/>, clamped to the balance; returns what was taken.</summary>
    uint Withdraw(uint amount);

    /// <summary>Refunds <paramref name="amount"/>. Zero is a no-op.</summary>
    void Deposit(uint amount);
}

/// <summary>
/// Binds <see cref="IPlayerFunds"/> to the live <see cref="BankAccount"/> - the ledger the
/// rest of the engine actually funds, spends, displays and persists. See this file's header
/// for why the charge is silent.
/// </summary>
public sealed class BankAccountFunds : IPlayerFunds
{
    private readonly BankAccount _account;

    public BankAccountFunds(BankAccount account)
    {
        _account = account;
    }

    /// <summary>The wrapped account, so callers can assert on the real balance.</summary>
    public BankAccount Account => _account;

    public bool CanAfford(uint amount) => _account.Money >= amount;

    public uint Withdraw(uint amount) => _account.Withdraw(amount, playSound: false);

    public void Deposit(uint amount) => _account.Deposit(amount, playSound: false);
}

/// <summary>
/// The production ledger resolver. This is the single place that decides WHICH ledger a
/// player's money lives in; every order handler goes through it.
/// </summary>
public static class PlayerFunds
{
    /// <summary>
    /// Resolves <paramref name="player"/>'s live money ledger, or null for a null player.
    /// Matches the <c>CastleBankResolver</c> delegate shape, so
    /// <c>new CastleOrderHandler(engine, PlayerFunds.ForPlayer)</c> is the production wiring.
    /// </summary>
    public static IPlayerFunds? ForPlayer(Player? player)
        => player?.BankAccount is { } account ? new BankAccountFunds(account) : null;
}
