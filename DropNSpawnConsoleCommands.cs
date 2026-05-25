using System;
namespace DropNSpawn;

internal static class DropNSpawnConsoleCommands
{
    private const string WriteFullCommandName = "dns:full";
    private const string WriteReferenceCommandName = "dns:reference";
    private const string InspectCommandName = "dns:inspect";
    private const string BossStoneCommandName = "dns:bossstone";
    private static readonly System.Collections.Generic.List<string> ScopedDomainTabOptions = new()
    {
        "object",
        "character",
        "spawner",
        "location",
        "spawnsystem",
        "all"
    };
    private static readonly System.Collections.Generic.List<string> InspectTabOptions = new()
    {
        "spawner",
        "bossstone"
    };
    private static readonly System.Collections.Generic.List<string> BossStoneTabOptions = new()
    {
        "reset"
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
            "Write non-loaded full scaffold YAML files for object/character/spawner/location/spawnsystem entries with explicit defaults.",
            WriteFullScaffoldFiles,
            optionsFetcher: GetScopedDomainTabOptions);
        new Terminal.ConsoleCommand(
            WriteReferenceCommandName,
            "Write current generated reference YAML files for object/character/spawner/location/spawnsystem.",
            WriteReferenceFiles,
            optionsFetcher: GetScopedDomainTabOptions);
        new Terminal.ConsoleCommand(
            InspectCommandName,
            "Inspect the current hovered/aimed runtime target. Currently supports: spawner, bossstone.",
            InspectRuntimeTarget,
            optionsFetcher: GetInspectTabOptions);
        new Terminal.ConsoleCommand(
            BossStoneCommandName,
            "Reset per-player boss stone state. Syntax: dns:bossstone reset <exactPlayerName>",
            HandleBossStoneCommand,
            isCheat: true,
            optionsFetcher: GetBossStoneTabOptions,
            onlyAdmin: true);
    }

    private static System.Collections.Generic.List<string> GetScopedDomainTabOptions()
    {
        return ScopedDomainTabOptions;
    }

    private static System.Collections.Generic.List<string> GetInspectTabOptions()
    {
        return InspectTabOptions;
    }

    private static System.Collections.Generic.List<string> GetBossStoneTabOptions()
    {
        return BossStoneTabOptions;
    }

    private static void WriteFullScaffoldFiles(Terminal.ConsoleEventArgs args)
    {
        if (!TryParseScope(args, WriteFullCommandName, out bool includeObject, out bool includeCharacter, out bool includeSpawner, out bool includeLocation, out bool includeSpawnSystem))
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

        if (includeLocation)
        {
            if (LocationManager.TryWriteFullScaffoldConfigurationFile(out string locationPath, out string locationError))
            {
                args.Context?.AddString($"Wrote location full scaffold to {locationPath}");
            }
            else
            {
                args.Context?.AddString(locationError);
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
    }

    private static void WriteReferenceFiles(Terminal.ConsoleEventArgs args)
    {
        if (!TryParseScope(args, WriteReferenceCommandName, out bool includeObject, out bool includeCharacter, out bool includeSpawner, out bool includeLocation, out bool includeSpawnSystem))
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

        if (includeLocation)
        {
            LocationManager.RefreshReferenceConfigurationFile();
            args.Context?.AddString("Updated location reference configuration.");
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
            case "bossstone":
                if (BossStonePerPlayerRuntime.TryInspectCurrentTarget(out string[] bossStoneLines, out string bossStoneError))
                {
                    foreach (string line in bossStoneLines)
                    {
                        args.Context?.AddString(line);
                    }
                }
                else
                {
                    args.Context?.AddString(bossStoneError);
                }

                return;
            default:
                args.Context?.AddString($"Syntax: {InspectCommandName} spawner");
                args.Context?.AddString($"Syntax: {InspectCommandName} bossstone");
                return;
        }
    }

    private static void HandleBossStoneCommand(Terminal.ConsoleEventArgs args)
    {
        string action = args.Length >= 2 ? (args[1] ?? "").Trim().ToLowerInvariant() : "";
        switch (action)
        {
            case "reset":
                const string resetPrefix = BossStoneCommandName + " reset";
                string targetPlayerName = args.FullLine.Length > resetPrefix.Length
                    ? args.FullLine.Substring(resetPrefix.Length).Trim()
                    : "";
                if (BossStonePerPlayerRuntime.TryRequestReset(targetPlayerName, out string resetMessage))
                {
                    args.Context?.AddString(resetMessage);
                }
                else
                {
                    args.Context?.AddString(resetMessage);
                }

                return;
            default:
                args.Context?.AddString($"Syntax: {BossStoneCommandName} reset <exactPlayerName>");
                return;
        }
    }

    private static bool TryParseScope(Terminal.ConsoleEventArgs args, string commandName, out bool includeObject, out bool includeCharacter, out bool includeSpawner, out bool includeLocation, out bool includeSpawnSystem)
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
                includeLocation = true;
                includeSpawnSystem = true;
                return true;
            case "object":
                includeObject = true;
                includeCharacter = false;
                includeSpawner = false;
                includeLocation = false;
                includeSpawnSystem = false;
                return true;
            case "character":
                includeObject = false;
                includeCharacter = true;
                includeSpawner = false;
                includeLocation = false;
                includeSpawnSystem = false;
                return true;
            case "spawner":
                includeObject = false;
                includeCharacter = false;
                includeSpawner = true;
                includeLocation = false;
                includeSpawnSystem = false;
                return true;
            case "location":
                includeObject = false;
                includeCharacter = false;
                includeSpawner = false;
                includeLocation = true;
                includeSpawnSystem = false;
                return true;
            case "spawnsystem":
                includeObject = false;
                includeCharacter = false;
                includeSpawner = false;
                includeLocation = false;
                includeSpawnSystem = true;
                return true;
            default:
                includeObject = false;
                includeCharacter = false;
                includeSpawner = false;
                includeLocation = false;
                includeSpawnSystem = false;
                args.Context?.AddString($"Syntax: {commandName} [object|character|spawner|location|spawnsystem|all]");
                return false;
        }
    }
}
