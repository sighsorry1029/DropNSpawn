using System;
namespace DropNSpawn;

internal static class DropNSpawnConsoleCommands
{
    private const string WriteFullCommandName = "dns:full";
    private const string WriteReferenceCommandName = "dns:reference";
    private const string InspectCommandName = "dns:inspect";
    private static readonly System.Collections.Generic.List<string> ScopedDomainTabOptions = new()
    {
        "object",
        "character",
        "spawner",
        "spawnsystem",
        "event",
        "events",
        "all"
    };
    private static readonly System.Collections.Generic.List<string> InspectTabOptions = new()
    {
        "spawner"
    };
    private static bool _registered;

    internal static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        new Terminal.ConsoleCommand(
            WriteFullCommandName,
            "Write non-loaded full scaffold YAML files for object/character/spawner/spawnsystem entries with explicit defaults. Event full scaffold is folded into the event reference.",
            WriteFullScaffoldFiles,
            optionsFetcher: GetScopedDomainTabOptions);
        new Terminal.ConsoleCommand(
            WriteReferenceCommandName,
            "Write current generated reference YAML files for object/character/spawner/spawnsystem/event.",
            WriteReferenceFiles,
            optionsFetcher: GetScopedDomainTabOptions);
        new Terminal.ConsoleCommand(
            InspectCommandName,
            "Inspect the current hovered/aimed runtime target. Currently supports: spawner.",
            InspectRuntimeTarget,
            optionsFetcher: GetInspectTabOptions);
    }

    private static System.Collections.Generic.List<string> GetScopedDomainTabOptions()
    {
        return ScopedDomainTabOptions;
    }

    private static System.Collections.Generic.List<string> GetInspectTabOptions()
    {
        return InspectTabOptions;
    }

    private static void WriteFullScaffoldFiles(Terminal.ConsoleEventArgs args)
    {
        if (!TryParseScope(args, WriteFullCommandName, out bool includeObject, out bool includeCharacter, out bool includeSpawner, out bool includeSpawnSystem, out bool includeEvent))
        {
            return;
        }

        if (includeObject)
        {
            if (ObjectDropManager.TryWriteFullScaffoldConfigurationFile(out string objectPath, out string objectError))
            {
                args.Context?.AddString($"Wrote object full scaffold to {objectPath}");
            }
            else
            {
                args.Context?.AddString(objectError);
            }
        }

        if (includeCharacter)
        {
            if (CharacterDropManager.TryWriteFullScaffoldConfigurationFile(out string characterPath, out string characterError))
            {
                args.Context?.AddString($"Wrote character full scaffold to {characterPath}");
            }
            else
            {
                args.Context?.AddString(characterError);
            }
        }

        if (includeSpawner)
        {
            if (SpawnerManager.TryWriteFullScaffoldConfigurationFile(out string spawnerPath, out string spawnerError))
            {
                args.Context?.AddString($"Wrote spawner full scaffold to {spawnerPath}");
            }
            else
            {
                args.Context?.AddString(spawnerError);
            }
        }

        if (includeSpawnSystem)
        {
            if (SpawnSystemManager.TryWriteFullScaffoldConfigurationFile(out string spawnSystemPath, out string spawnSystemError))
            {
                args.Context?.AddString($"Wrote spawnsystem full scaffold to {spawnSystemPath}");
            }
            else
            {
                args.Context?.AddString(spawnSystemError);
            }
        }

        if (includeEvent)
        {
            if (EventManager.TryWriteFullScaffoldConfigurationFile(out string eventPath, out string eventError))
            {
                args.Context?.AddString($"Wrote event full scaffold to {eventPath}");
            }
            else
            {
                args.Context?.AddString(eventError);
            }
        }
    }

    private static void WriteReferenceFiles(Terminal.ConsoleEventArgs args)
    {
        if (!TryParseScope(args, WriteReferenceCommandName, out bool includeObject, out bool includeCharacter, out bool includeSpawner, out bool includeSpawnSystem, out bool includeEvent))
        {
            return;
        }

        if (includeObject)
        {
            ObjectDropManager.RefreshReferenceConfigurationFile();
            args.Context?.AddString("Updated object reference configuration.");
        }

        if (includeCharacter)
        {
            CharacterDropManager.RefreshReferenceConfigurationFile();
            args.Context?.AddString("Updated character reference configuration.");
        }

        if (includeSpawner)
        {
            SpawnerManager.RefreshReferenceConfigurationFile();
            args.Context?.AddString("Updated spawner reference configuration.");
        }

        if (includeSpawnSystem)
        {
            if (SpawnSystemManager.TryWriteReferenceConfigurationFile(out string spawnSystemPath, out string spawnSystemError))
            {
                args.Context?.AddString($"Wrote spawnsystem reference to {spawnSystemPath}");
            }
            else
            {
                args.Context?.AddString(spawnSystemError);
            }
        }

        if (includeEvent)
        {
            if (EventManager.TryWriteReferenceConfigurationFile(out string eventPath, out string eventError))
            {
                args.Context?.AddString($"Wrote event reference to {eventPath}");
            }
            else
            {
                args.Context?.AddString(eventError);
            }
        }
    }

    private static void InspectRuntimeTarget(Terminal.ConsoleEventArgs args)
    {
        string scope = args.Length >= 2 ? (args[1] ?? "").Trim().ToLowerInvariant() : "";
        switch (scope)
        {
            case "spawner":
                if (SpawnerManager.TryInspectCurrentTarget(out string[] lines, out string error))
                {
                    foreach (string line in lines)
                    {
                        args.Context?.AddString(line);
                    }
                }
                else
                {
                    args.Context?.AddString(error);
                }

                return;
            default:
                args.Context?.AddString($"Syntax: {InspectCommandName} spawner");
                return;
        }
    }

    private static bool TryParseScope(Terminal.ConsoleEventArgs args, string commandName, out bool includeObject, out bool includeCharacter, out bool includeSpawner, out bool includeSpawnSystem, out bool includeEvent)
    {
        string scope = args.Length >= 2 ? (args[1] ?? "").Trim().ToLowerInvariant() : "all";
        if (scope.Length == 0)
        {
            scope = "all";
        }

        switch (scope)
        {
            case "all":
                includeObject = true;
                includeCharacter = true;
                includeSpawner = true;
                includeSpawnSystem = true;
                includeEvent = true;
                return true;
            case "object":
                includeObject = true;
                includeCharacter = false;
                includeSpawner = false;
                includeSpawnSystem = false;
                includeEvent = false;
                return true;
            case "character":
                includeObject = false;
                includeCharacter = true;
                includeSpawner = false;
                includeSpawnSystem = false;
                includeEvent = false;
                return true;
            case "spawner":
                includeObject = false;
                includeCharacter = false;
                includeSpawner = true;
                includeSpawnSystem = false;
                includeEvent = false;
                return true;
            case "spawnsystem":
                includeObject = false;
                includeCharacter = false;
                includeSpawner = false;
                includeSpawnSystem = true;
                includeEvent = false;
                return true;
            case "event":
            case "events":
                includeObject = false;
                includeCharacter = false;
                includeSpawner = false;
                includeSpawnSystem = false;
                includeEvent = true;
                return true;
            default:
                includeObject = false;
                includeCharacter = false;
                includeSpawner = false;
                includeSpawnSystem = false;
                includeEvent = false;
                args.Context?.AddString($"Syntax: {commandName} [object|character|spawner|spawnsystem|event|all]");
                return false;
        }
    }
}
