namespace Wcs.DecisionIntelligence;

public sealed record DecisionMetricsSnapshot(
    long ProposalGeneratedTotal,
    long ProposalBlockedTotal,
    long ProposalApprovedTotal,
    long ProposalRejectedTotal,
    long ProposalExpiredTotal,
    long ProposalOutcomeMatchedTotal,
    decimal ProposalEstimatedBenefit,
    decimal ProposalActualBenefit);

public sealed class DecisionMetrics
{
    private long _generated;
    private long _blocked;
    private long _approved;
    private long _rejected;
    private long _expired;
    private long _outcomeMatched;
    private decimal _estimatedBenefit;
    private decimal _actualBenefit;
    private readonly object _benefitGate = new();

    public void Generated(bool blocked, decimal estimatedBenefit)
    {
        Interlocked.Increment(ref _generated);
        if (blocked) Interlocked.Increment(ref _blocked);
        lock (_benefitGate) _estimatedBenefit += estimatedBenefit;
    }

    public void Approved() => Interlocked.Increment(ref _approved);
    public void Rejected() => Interlocked.Increment(ref _rejected);
    public void Expired() => Interlocked.Increment(ref _expired);

    public void OutcomeMatched(decimal? actualBenefit)
    {
        Interlocked.Increment(ref _outcomeMatched);
        if (actualBenefit is not null)
            lock (_benefitGate) _actualBenefit += actualBenefit.Value;
    }

    public DecisionMetricsSnapshot Snapshot()
    {
        lock (_benefitGate)
            return new DecisionMetricsSnapshot(
                Interlocked.Read(ref _generated), Interlocked.Read(ref _blocked), Interlocked.Read(ref _approved),
                Interlocked.Read(ref _rejected), Interlocked.Read(ref _expired), Interlocked.Read(ref _outcomeMatched),
                _estimatedBenefit, _actualBenefit);
    }
}
