namespace DropNSpawn;

internal sealed class SpawnSystemTransportHooks : DomainTransportHooks
{
    internal static SpawnSystemTransportHooks Instance { get; } = new();
}
