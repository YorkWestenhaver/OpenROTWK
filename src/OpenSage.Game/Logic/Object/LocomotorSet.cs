using System;
using System.Collections.Generic;
using OpenSage.Diagnostics;

namespace OpenSage.Logic.Object;

public sealed class LocomotorSet : IPersistableObject
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// <see cref="DegradeLog"/> category for the unresolved-locomotor guard in
    /// <see cref="Initialize"/>.
    /// </summary>
    private const string UnresolvedLocomotorCategory = "LocomotorSet.UnresolvedLocomotor";

    private readonly GameObject _gameObject;
    private readonly IGameEngine _gameEngine;
    private readonly List<Locomotor> _locomotors;
    private Surfaces _surfaces;

    public LocomotorSet(GameObject gameObject, IGameEngine gameEngine)
    {
        _gameObject = gameObject;
        _gameEngine = gameEngine;
        _locomotors = new List<Locomotor>();
    }

    public void Initialize(LocomotorSetTemplate locomotorSetTemplate)
    {
        _surfaces = Surfaces.None;

        foreach (var locomotorTemplateReference in locomotorSetTemplate.Locomotors)
        {
            var locomotorTemplate = locomotorTemplateReference.Value;

            // R15 L1-11: a LocomotorSet may name a Locomotor block that does not exist in the
            // loaded INI corpus, in which case the lazy reference resolves to null. Both uses
            // below dereference it — Locomotor's constructor reads CloseEnoughDist off the
            // template on its first statement — so an unresolved name used to NRE straight out
            // of GameObject's constructor during map load and terminate the process
            // (RohanRoyalBannerStructure on "map good fords of isen" in the R15 AotR sweep).
            // Degrade: this locomotor simply does not exist for the object, which then has no
            // locomotor for those surfaces and does not move; report once per object template.
            if (locomotorTemplate == null)
            {
                var templateName = _gameObject?.Definition?.Name;
                if (DegradeLog.ShouldReport(UnresolvedLocomotorCategory, templateName))
                {
                    Logger.Warn(
                        $"Object template '{DegradeLog.Normalize(templateName)}' uses a LocomotorSet " +
                        "naming a Locomotor block that is not defined in the loaded INI corpus " +
                        "(the reference resolved to null); skipping it. The object may not move.");
                }

                continue;
            }

            _locomotors.Add(new Locomotor(
                _gameEngine,
                locomotorTemplate,
                locomotorSetTemplate.Speed));

            _surfaces |= locomotorTemplate.Surfaces;
        }
    }

    public void Reset()
    {
        _locomotors.Clear();
    }

    public Locomotor GetLocomotorForSurfaces(Surfaces surfaces)
    {
        foreach (var locomotor in _locomotors)
        {
            if ((locomotor.LocomotorTemplate.Surfaces & surfaces) != 0)
            {
                return locomotor;
            }
        }
        return null;
    }

    public Locomotor GetLocomotor(string locomotorTemplateName)
    {
        foreach (var locomotor in _locomotors)
        {
            if (locomotor.LocomotorTemplate.Name == locomotorTemplateName)
            {
                return locomotor;
            }
        }

        throw new InvalidOperationException();
    }

    public void Persist(StatePersister reader)
    {
        if (reader.Mode == StatePersistMode.Read)
        {
            _locomotors.Clear();
        }

        reader.PersistVersion(1);

        var numLocomotorTemplates = (ushort)_locomotors.Count;
        reader.PersistUInt16(ref numLocomotorTemplates, "NumLocomotors");

        reader.BeginArray("Locomotors");
        if (reader.Mode == StatePersistMode.Read)
        {
            for (var i = 0; i < numLocomotorTemplates; i++)
            {
                reader.BeginObject();

                var locomotorTemplateName = "";
                reader.PersistAsciiString(ref locomotorTemplateName, "TemplateName");

                var locomotorTemplate = reader.AssetStore.LocomotorTemplates.GetByName(locomotorTemplateName);

                var locomotor = new Locomotor(_gameEngine, locomotorTemplate, 100);

                reader.PersistObject(locomotor);

                _locomotors.Add(locomotor);

                reader.EndArray();
            }
        }
        else
        {
            foreach (var locomotor in _locomotors)
            {
                reader.BeginObject();

                var templateName = locomotor.LocomotorTemplate.Name;
                reader.PersistAsciiString(ref templateName);

                reader.PersistObject(locomotor);

                reader.EndObject();
            }
        }
        reader.EndArray();

        reader.PersistEnumFlags(ref _surfaces);

        reader.SkipUnknownBytes(1);
    }
}
