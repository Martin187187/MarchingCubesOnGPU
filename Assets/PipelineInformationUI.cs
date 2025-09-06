using System.Text;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class PipelineInformationUI : MonoBehaviour
{
    [Header("Refs")]
    public TerrainController controller;
    public TMP_Text text;

    [Header("Options")]
    public bool showPerStageHistogram = true;
    public bool showQueueCounts = true;

    // reuse dictionary to avoid GC
    private readonly Dictionary<Stage,int> hist = new();

    void Update()
    {
        if (controller == null || text == null) return;

        var sb = new StringBuilder(256);

        // Player chunk info
        var pc = controller.CurrentPlayerChunk;
        if (pc.HasValue) sb.AppendLine($"PlayerChunk: {pc.Value.x},{pc.Value.y},{pc.Value.z}");
        else sb.AppendLine("PlayerChunk: (none)");

        // Queue sizes
        if (showQueueCounts)
        {
            sb.AppendLine("Queues:");
            sb.Append("  Raw: ").Append(controller.QueueRawCount)
              .Append(" | Density→Struct: ").Append(controller.QueueDensityCount)
              .Append(" | Struct→Mesh: ").Append(controller.QueueStructureCount).AppendLine();

            sb.Append("  MeshPrio: ").Append(controller.QueueMeshPrioCount)
              .Append(" | Mesh: ").Append(controller.QueueMeshCount)
              .Append(" | Collider: ").Append(controller.QueueColliderCount).AppendLine();
        }

        // Per-stage histogram
        if (showPerStageHistogram)
        {
            controller.GetStageHistogram(hist);
            sb.AppendLine("Stages:");
            // stable order (customize to your enum)
            AppendStage(sb, hist, Stage.Raw,               "Raw");
            AppendStage(sb, hist, Stage.DensityCompleted,      "DensityReady");
            AppendStage(sb, hist, Stage.StructureCompleted,"Structure");
            AppendStage(sb, hist, Stage.MeshCompleted,     "Mesh");
            AppendStage(sb, hist, Stage.Finished, "Collider");
        }

        // loaded count
        sb.Append("Loaded: ").Append(controller.LoadedCount);

        text.text = sb.ToString();
    }

    private static void AppendStage(StringBuilder sb, Dictionary<Stage,int> hist, Stage s, string label)
    {
        hist.TryGetValue(s, out var c);
        sb.Append("  ").Append(label).Append(": ").Append(c).AppendLine();
    }
}
