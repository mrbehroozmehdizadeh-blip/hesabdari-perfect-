using Samaneh.Domain;

namespace Samaneh.Domain.Tests;

public sealed class JournalLifecycleTests
{
    [Fact]
    public void Submit_TransitionsDraftToPendingApproval()
    {
        Assert.Equal(JournalStatus.PendingApproval, JournalLifecycle.Submit(JournalStatus.Draft));
    }

    [Fact]
    public void Submit_RejectsNonDraftStatuses()
    {
        Assert.Throws<InvalidOperationException>(() => JournalLifecycle.Submit(JournalStatus.PendingApproval));
        Assert.Throws<InvalidOperationException>(() => JournalLifecycle.Submit(JournalStatus.Approved));
        Assert.Throws<InvalidOperationException>(() => JournalLifecycle.Submit(JournalStatus.Posted));
    }

    [Fact]
    public void Approve_RequiresDifferentCreatorAndApprover()
    {
        var creator = Guid.NewGuid();
        var approver = Guid.NewGuid();

        Assert.Equal(JournalStatus.Approved,
            JournalLifecycle.Approve(JournalStatus.PendingApproval, creator, approver));
        Assert.Throws<InvalidOperationException>(() =>
            JournalLifecycle.Approve(JournalStatus.PendingApproval, creator, creator));
    }

    [Fact]
    public void Approve_RejectsWrongStatusAndMissingIdentity()
    {
        Assert.Throws<InvalidOperationException>(() =>
            JournalLifecycle.Approve(JournalStatus.Draft, Guid.NewGuid(), Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() =>
            JournalLifecycle.Approve(JournalStatus.PendingApproval, Guid.Empty, Guid.NewGuid()));
    }

    [Fact]
    public void Post_RequiresApproval()
    {
        Assert.Equal(JournalStatus.Posted, JournalLifecycle.Post(JournalStatus.Approved));
        Assert.Throws<InvalidOperationException>(() => JournalLifecycle.Post(JournalStatus.Draft));
        Assert.Throws<InvalidOperationException>(() => JournalLifecycle.Post(JournalStatus.PendingApproval));
    }

    [Fact]
    public void Reverse_RequiresPostedJournal()
    {
        Assert.Equal(JournalStatus.Reversed, JournalLifecycle.Reverse(JournalStatus.Posted));
        Assert.Throws<InvalidOperationException>(() => JournalLifecycle.Reverse(JournalStatus.Approved));
    }
}
