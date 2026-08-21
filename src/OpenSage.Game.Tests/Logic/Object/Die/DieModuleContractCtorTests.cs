// Guard tests for the SHARED DieModule contract ctor (experiment-round-4 §4.1 batch
// economies). Nine of the eleven Die-batch branches independently added a byte-identical
// `DieModule(GameObject, ISimContext, DieModuleData)` overload to the category base, which is
// the definition of something that belongs on the base branch instead. Promoting it removes
// nine merge conflicts, but a promoted member with no test on the base branch would be free to
// regress silently between now and the first Die merge - hence this file.
//
// What is actually being asserted:
//   1. the ctor forwards to BehaviorModule's ISimContext ctor, so Context is populated (this is
//      the whole point of the contract ctor - a ported module's only door to the sim), and
//   2. the shared applicability gate in IDieModule.OnDie still runs for a module built through
//      the NEW ctor, i.e. promoting it did not accidentally bypass DieLogicData.
//
// The subject is a test-only DieModule subclass rather than a real port: on this branch no Die
// class is ported yet (the ports live on the eleven die/* branches), and the promotion has to
// be guarded here, where it lands.

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Die;

public class DieModuleContractCtorTests
{
    private const string Definitions = @"
Object CtorTestGrunt
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    /// <summary>
    /// A minimal PORTED-shape Die module: it constructs through the contract ctor only, and
    /// records what it saw so the test can assert on it. Its <see cref="SeenContext"/> is the
    /// protected <c>Context</c> the ctor is supposed to have populated.
    /// </summary>
    private sealed class ProbeDieModule : DieModule
    {
        public ProbeDieModule(GameObject gameObject, ISimContext context, DieModuleData moduleData)
            : base(gameObject, context, moduleData)
        {
        }

        public ISimContext SeenContext => Context;

        public int DieCallCount { get; private set; }

        public DeathType LastDeathType { get; private set; }

        protected override void Die(in DamageInfoInput damageInput)
        {
            DieCallCount++;
            LastDeathType = damageInput.DeathType;
        }
    }

    /// <summary>
    /// Concrete <see cref="DieModuleData"/> with a default (unfiltered) <c>DieLogicData</c>:
    /// DeathTypes / RequiredStatus / ExemptStatus all unset, which per
    /// <c>DieLogicData.IsDieApplicable</c> means "applicable to every death". That is the right
    /// baseline for a ctor test - the per-branch filter behavior is DeathTriggerKitTests' job.
    /// </summary>
    private sealed class ProbeDieModuleData : DieModuleData
    {
    }

    private static (HeadlessSimGame Game, GameObject Object) NewGameWithObject()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: 0xC70Bu);
        game.LoadIniText(Definitions);
        var gameObject = game.SpawnObject("CtorTestGrunt", game.CivilianPlayer, new Vector3(0, 0, 0));
        return (game, gameObject);
    }

    [Fact]
    public void ContractCtor_PopulatesContext()
    {
        var (game, gameObject) = NewGameWithObject();
        var context = game.GameEngine.SimContext;

        var module = new ProbeDieModule(gameObject, context, new ProbeDieModuleData());

        // The contract ctor's entire job: BehaviorModule.Context is the door, and it is open.
        Assert.NotNull(module.SeenContext);
        Assert.Same(context, module.SeenContext);
    }

    [Fact]
    public void ContractCtor_StillRoutesThroughTheSharedApplicabilityGate()
    {
        var (game, gameObject) = NewGameWithObject();
        var module = new ProbeDieModule(
            gameObject, game.GameEngine.SimContext, new ProbeDieModuleData());

        Assert.Equal(0, module.DieCallCount);

        ((IDieModule)module).OnDie(new DamageInfoInput(source: null)
        {
            DamageType = DamageType.Unresistable,
            DeathType = DeathType.Burned,
            Amount = 100f,
        });

        // The gate passed an unfiltered DieLogicData through, and the death type survived the
        // trip from the dispatch into the subclass - the two facts every Die port depends on.
        Assert.Equal(1, module.DieCallCount);
        Assert.Equal(DeathType.Burned, module.LastDeathType);
    }
}
