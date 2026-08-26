namespace JndUfo
{
    /// <summary>
    /// Shared surface between the legacy two-phase coarse/fine staircase (UfoStaircase)
    /// and the Bayesian QUEST+ staircase (QuestPlusStaircase), so PerturbationController
    /// can drive either one per test mode without caring which algorithm is behind it.
    /// </summary>
    public interface IJndStaircase
    {
        float CurrentValue { get; }
        int   TrialCount   { get; }
        bool  IsFinished   { get; }
        float RecordResponse(bool isHit);
        float JndEstimate();
    }
}
