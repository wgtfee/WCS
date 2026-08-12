namespace Wcs.Desktop.ViewModels;

using Wcs.Desktop.Services;

public partial class SimulationVerificationViewModel
{
    partial void OnStatusTextChanged(string value)
    {
        if (ScenarioDataRows.Count == 0 && !string.IsNullOrWhiteSpace(ScenarioJson))
            RebuildScenarioDataPreview(ScenarioJson);
    }

    partial void OnSelectedRunChanged(SimulationRunDto? value)
    {
        ClearCheckpointStatePreview();
        CheckpointHash = "-";
        CheckpointStateText = value is null
            ? "尚未选择 Run"
            : value.IsTerminal
                ? "终态 Run 不开放运行中 Checkpoint；Scenario 数据和终态 Evidence 仍可查看。"
                : "已选择 Run；点击读取 Checkpoint 后显示完整状态数据。";
    }

    partial void OnCheckpointHashChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "-" || SelectedRun is null)
        {
            ClearCheckpointStatePreview();
            return;
        }

        _ = LoadCheckpointStatePreviewAsync(SelectedRun.RunId, value);
    }

    private async Task LoadCheckpointStatePreviewAsync(Guid runId, string expectedCheckpointHash)
    {
        try
        {
            var checkpoint = await _api.GetCheckpointAsync(runId).ConfigureAwait(true);
            if (SelectedRun?.RunId != runId || !string.Equals(CheckpointHash, expectedCheckpointHash, StringComparison.Ordinal))
                return;

            ApplyCheckpointStatePreview(checkpoint.StateJson);
        }
        catch
        {
            // The primary checkpoint command already reports API errors. This secondary read only
            // enriches the Desktop data view and must not change execution or governance behavior.
            ClearCheckpointStatePreview();
        }
    }
}
