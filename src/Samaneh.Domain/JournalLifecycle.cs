namespace Samaneh.Domain;

public enum JournalStatus
{
    Draft = 0,
    PendingApproval = 1,
    Approved = 2,
    Posted = 3,
    Reversed = 4
}

/// <summary>Pure domain rules for the journal approval lifecycle; persistence is handled by the application.</summary>
public static class JournalLifecycle
{
    public static JournalStatus Submit(JournalStatus current)
    {
        if (current != JournalStatus.Draft)
            throw new InvalidOperationException("Only a draft journal can be submitted for approval.");
        return JournalStatus.PendingApproval;
    }

    public static JournalStatus Approve(JournalStatus current, Guid createdBy, Guid approver)
    {
        if (current != JournalStatus.PendingApproval)
            throw new InvalidOperationException("Only a pending journal can be approved.");
        if (createdBy == Guid.Empty || approver == Guid.Empty)
            throw new ArgumentException("Creator and approver identities are required.");
        if (createdBy == approver)
            throw new InvalidOperationException("The journal creator cannot approve their own journal.");
        return JournalStatus.Approved;
    }

    public static JournalStatus Post(JournalStatus current)
    {
        if (current != JournalStatus.Approved)
            throw new InvalidOperationException("Only an approved journal can be posted.");
        return JournalStatus.Posted;
    }

    public static JournalStatus Reverse(JournalStatus current)
    {
        if (current != JournalStatus.Posted)
            throw new InvalidOperationException("Only a posted journal can be reversed through a reversal entry.");
        return JournalStatus.Reversed;
    }
}
