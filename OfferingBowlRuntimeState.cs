using UnityEngine;

namespace DropNSpawn;

internal sealed class OfferingBowlRuntimeState : MonoBehaviour
{
    public float RespawnMinutes { get; set; }
    public long LocalLastUseTicks { get; set; }
    public ExpandWorldSpawnDataPayload? SpawnPayload { get; set; }
}
