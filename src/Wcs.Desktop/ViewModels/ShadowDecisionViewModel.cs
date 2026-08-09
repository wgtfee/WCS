namespace Wcs.Desktop.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using Wcs.Desktop.Services;

public partial class ShadowDecisionViewModel : ViewModelBase
{
    private readonly IShadowDecisionApiService _api;

    public ObservableCollection<DecisionProposalDto> Proposals { get; } = [];
    public ObservableCollection<DecisionConstraintDto> Constraints { get; } = [];
    public ObservableCollection<DecisionApprovalDto> Approvals { get; } = [];

    [ObservableProperty] private DecisionProposalDto? _selectedProposal;
    [ObservableProperty] private string _statusText = "尚未刷新";
    [ObservableProperty] private string _detailText = "请选择 Proposal";
    [ObservableProperty] private int _proposalCount;
    [ObservableProperty] private int _blockedCount;
    [ObservableProperty] private int _approvedCount;
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _actor = string.Empty;
    [ObservableProperty] private string _reason = string.Empty;
    [ObservableProperty] private string _correlationId = Guid.NewGuid().ToString("N");
    [ObservableProperty] private string _outcomeType = "ObservedResult";
    [ObservableProperty] private string _actualReference = string.Empty;
    [ObservableProperty] private decimal? _actualBenefit;
    [ObservableProperty] private string _outcomeEvidenceHash = string.Empty;

    public string SafetyText => "P3 是 Proposal 治理中心。Approve/Reject 只改变建议治理状态；Outcome 只回填实际结果。任何按钮都不会发送 CommandBus、写 PLC、改调度、路权或交通状态。";

    public ShadowDecisionViewModel(IShadowDecisionApiService api) => _api = api;

    protected override Task OnInitializeAsync() => RefreshAsync();

    partial void OnSelectedProposalChanged(DecisionProposalDto? value)
    {
        if (value is null) return;
        OutcomeEvidenceHash = value.Evidence.EvidenceHash;
        _ = LoadSelectedAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "正在读取 Shadow Decision Proposals...";
        try
        {
            var values = await _api.GetProposalsAsync(200).ConfigureAwait(true);
            Proposals.Clear();
            foreach (var item in values) Proposals.Add(item);
            ProposalCount = Proposals.Count;
            BlockedCount = Proposals.Count(x => string.Equals(x.Status, "Blocked", StringComparison.OrdinalIgnoreCase));
            ApprovedCount = Proposals.Count(x => string.Equals(x.Status, "Approved", StringComparison.OrdinalIgnoreCase));
            PendingCount = Proposals.Count(x => string.Equals(x.Status, "Shadow", StringComparison.OrdinalIgnoreCase) || string.Equals(x.Status, "PendingApproval", StringComparison.OrdinalIgnoreCase));
            StatusText = $"已刷新：{DateTime.Now:yyyy-MM-dd HH:mm:ss} · Proposal {ProposalCount} · Pending {PendingCount} · Blocked {BlockedCount} · Approved {ApprovedCount}";
        }
        catch (Exception ex) { StatusText = $"读取失败（保持 proposal-only）：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task LoadSelectedAsync()
    {
        if (IsBusy || SelectedProposal is null) return;
        IsBusy = true;
        try
        {
            var detail = await _api.GetProposalAsync(SelectedProposal.ProposalId).ConfigureAwait(true);
            if (detail?.Proposal is null)
            {
                DetailText = "Proposal 不存在或当前环境已 fail-closed";
                return;
            }
            SelectedProposal = detail.Proposal;
            Constraints.Clear();
            foreach (var item in detail.Proposal.Constraints) Constraints.Add(item);
            Approvals.Clear();
            foreach (var item in detail.Approvals) Approvals.Add(item);
            var outcome = detail.Outcome is null
                ? "Outcome=未回填"
                : $"Outcome={detail.Outcome.OutcomeType}/{detail.Outcome.ActualReference}/{detail.Outcome.ActualBenefit}";
            DetailText = $"Action={detail.Proposal.Candidate.Action} · Score={detail.Proposal.Candidate.Score} · Model={detail.Proposal.Evidence.ModelId}/{detail.Proposal.Evidence.ModelVersion} · Snapshot={detail.Proposal.Evidence.FeatureSnapshotId} · {outcome}\n{detail.Proposal.Explanation.Summary}";
            OutcomeEvidenceHash = detail.Proposal.Evidence.EvidenceHash;
        }
        catch (Exception ex) { DetailText = $"读取详情失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private Task ApproveAsync() => TransitionAsync(true);

    [RelayCommand]
    private Task RejectAsync() => TransitionAsync(false);

    private async Task TransitionAsync(bool approve)
    {
        if (IsBusy || SelectedProposal is null || string.IsNullOrWhiteSpace(Actor) || string.IsNullOrWhiteSpace(Reason)) return;
        IsBusy = true;
        try
        {
            var request = new DecisionGovernanceActionDto
            {
                Actor = Actor.Trim(),
                Reason = Reason.Trim(),
                CorrelationId = string.IsNullOrWhiteSpace(CorrelationId) ? Guid.NewGuid().ToString("N") : CorrelationId.Trim(),
                IdempotencyKey = Guid.NewGuid().ToString("N")
            };
            if (approve) await _api.ApproveAsync(SelectedProposal.ProposalId, request).ConfigureAwait(true);
            else await _api.RejectAsync(SelectedProposal.ProposalId, request).ConfigureAwait(true);
            StatusText = approve ? "Proposal 已批准（治理状态变更，不执行控制）" : "Proposal 已拒绝";
        }
        catch (Exception ex) { StatusText = $"治理动作失败：{ex.Message}"; }
        finally { IsBusy = false; }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RecordOutcomeAsync()
    {
        if (IsBusy || SelectedProposal is null || string.IsNullOrWhiteSpace(OutcomeType) ||
            string.IsNullOrWhiteSpace(ActualReference) || string.IsNullOrWhiteSpace(OutcomeEvidenceHash)) return;
        IsBusy = true;
        try
        {
            await _api.RecordOutcomeAsync(SelectedProposal.ProposalId, new DecisionOutcomeRequestDto
            {
                OutcomeType = OutcomeType.Trim(),
                ActualReference = ActualReference.Trim(),
                ActualBenefit = ActualBenefit,
                ObservedAtUtc = DateTimeOffset.UtcNow,
                EvidenceHash = OutcomeEvidenceHash.Trim()
            }).ConfigureAwait(true);
            StatusText = "Outcome 已回填；仅用于评价/学习，不反向控制 WCS";
        }
        catch (Exception ex) { StatusText = $"Outcome 回填失败：{ex.Message}"; }
        finally { IsBusy = false; }
        await RefreshAsync();
    }
}
