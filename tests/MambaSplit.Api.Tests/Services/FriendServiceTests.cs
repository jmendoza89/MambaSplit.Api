using MambaSplit.Api.Domain;
using MambaSplit.Api.Exceptions;
using MambaSplit.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace MambaSplit.Api.Tests.Services;

public class FriendServiceTests
{
    private static UserEntity MakeUser(string email, string displayName) => new()
    {
        Id = Guid.NewGuid(),
        Email = email,
        PasswordHash = "hash",
        DisplayName = displayName,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static GroupEntity MakeGroup(Guid createdBy, string name = "Test Group") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        CreatedBy = createdBy,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static GroupMemberEntity MakeMember(Guid groupId, Guid userId, Role role = Role.MEMBER) => new()
    {
        Id = Guid.NewGuid(),
        GroupId = groupId,
        UserId = userId,
        Role = role,
        JoinedAt = DateTimeOffset.UtcNow,
    };

    // --- UpsertOnInviteSentAsync Tests ---

    [Fact]
    public async Task UpsertOnInviteSent_CreatesNewPendingRow_WhenNoExistingUser()
    {
        await using var ctx = await FriendTestContext.CreateAsync();
        var owner = MakeUser("owner@test.com", "Owner");
        ctx.Db.Users.Add(owner);
        await ctx.Db.SaveChangesAsync();

        await ctx.FriendService.UpsertOnInviteSentAsync(
            owner.Id, "stranger@test.com", "Stranger", CancellationToken.None);

        var fc = await ctx.Db.FriendConnections.SingleAsync();
        Assert.Equal(owner.Id, fc.OwnerUserId);
        Assert.Null(fc.FriendUserId);
        Assert.Equal("Stranger", fc.DisplayName);
        Assert.Equal("stranger@test.com", fc.NormalizedEmail);
        Assert.Equal("Pending", fc.Status);
        Assert.Null(fc.ConnectedAtUtc);
    }

    [Fact]
    public async Task UpsertOnInviteSent_CreatesConnectedRow_WhenUserAlreadyExists()
    {
        await using var ctx = await FriendTestContext.CreateAsync();
        var owner = MakeUser("owner@test.com", "Owner");
        var friend = MakeUser("friend@test.com", "Friend");
        ctx.Db.Users.AddRange(owner, friend);
        await ctx.Db.SaveChangesAsync();

        await ctx.FriendService.UpsertOnInviteSentAsync(
            owner.Id, "friend@test.com", null, CancellationToken.None);

        var fc = await ctx.Db.FriendConnections.SingleAsync();
        Assert.Equal(friend.Id, fc.FriendUserId);
        Assert.Equal("Friend", fc.DisplayName); // falls back to user's display name
        Assert.Equal("Connected", fc.Status);
        Assert.NotNull(fc.ConnectedAtUtc);
    }

    [Fact]
    public async Task UpsertOnInviteSent_UpdatesDisplayName_WhenRowAlreadyExists()
    {
        await using var ctx = await FriendTestContext.CreateAsync();
        var owner = MakeUser("owner@test.com", "Owner");
        ctx.Db.Users.Add(owner);
        await ctx.Db.SaveChangesAsync();

        await ctx.FriendService.UpsertOnInviteSentAsync(
            owner.Id, "stranger@test.com", "OldName", CancellationToken.None);
        await ctx.FriendService.UpsertOnInviteSentAsync(
            owner.Id, "stranger@test.com", "NewName", CancellationToken.None);

        var fc = await ctx.Db.FriendConnections.SingleAsync();
        Assert.Equal("NewName", fc.DisplayName);
    }

    [Fact]
    public async Task UpsertOnInviteSent_DoesNotDowngradeConnectedToPending()
    {
        await using var ctx = await FriendTestContext.CreateAsync();
        var owner = MakeUser("owner@test.com", "Owner");
        var friend = MakeUser("friend@test.com", "Friend");
        ctx.Db.Users.AddRange(owner, friend);
        await ctx.Db.SaveChangesAsync();

        // First invite creates Connected (user exists)
        await ctx.FriendService.UpsertOnInviteSentAsync(
            owner.Id, "friend@test.com", null, CancellationToken.None);

        var fc1 = await ctx.Db.FriendConnections.SingleAsync();
        Assert.Equal("Connected", fc1.Status);

        // Second invite to same email should not change status
        await ctx.FriendService.UpsertOnInviteSentAsync(
            owner.Id, "friend@test.com", "Updated Name", CancellationToken.None);

        var fc2 = await ctx.Db.FriendConnections.SingleAsync();
        Assert.Equal("Connected", fc2.Status);
        Assert.Equal("Updated Name", fc2.DisplayName);
    }

    // --- UpsertOnInviteAcceptedAsync Tests ---

    [Fact]
    public async Task UpsertOnInviteAccepted_CreatesBothDirections()
    {
        await using var ctx = await FriendTestContext.CreateAsync();
        var inviter = MakeUser("inviter@test.com", "Inviter");
        var acceptor = MakeUser("acceptor@test.com", "Acceptor");
        ctx.Db.Users.AddRange(inviter, acceptor);
        await ctx.Db.SaveChangesAsync();

        await ctx.FriendService.UpsertOnInviteAcceptedAsync(
            inviter.Id, acceptor.Id, CancellationToken.None);

        var connections = await ctx.Db.FriendConnections.ToListAsync();
        Assert.Equal(2, connections.Count);

        var inviterToAcceptor = connections.Single(c => c.OwnerUserId == inviter.Id);
        Assert.Equal(acceptor.Id, inviterToAcceptor.FriendUserId);
        Assert.Equal("Acceptor", inviterToAcceptor.DisplayName);
        Assert.Equal("Connected", inviterToAcceptor.Status);

        var acceptorToInviter = connections.Single(c => c.OwnerUserId == acceptor.Id);
        Assert.Equal(inviter.Id, acceptorToInviter.FriendUserId);
        Assert.Equal("Inviter", acceptorToInviter.DisplayName);
        Assert.Equal("Connected", acceptorToInviter.Status);
    }

    [Fact]
    public async Task UpsertOnInviteAccepted_UpgradesPendingToConnected()
    {
        await using var ctx = await FriendTestContext.CreateAsync();
        var inviter = MakeUser("inviter@test.com", "Inviter");
        var acceptor = MakeUser("acceptor@test.com", "Acceptor");
        ctx.Db.Users.AddRange(inviter, acceptor);
        await ctx.Db.SaveChangesAsync();

        // First: invite sent created a Pending row (simulated)
        ctx.Db.FriendConnections.Add(new FriendConnectionEntity
        {
            Id = Guid.NewGuid(),
            OwnerUserId = inviter.Id,
            FriendUserId = null,
            DisplayName = "Acceptor",
            NormalizedEmail = "acceptor@test.com",
            OriginalEmail = "acceptor@test.com",
            Status = "Pending",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
        });
        await ctx.Db.SaveChangesAsync();

        await ctx.FriendService.UpsertOnInviteAcceptedAsync(
            inviter.Id, acceptor.Id, CancellationToken.None);

        var connections = await ctx.Db.FriendConnections.ToListAsync();
        Assert.Equal(2, connections.Count);

        var inviterRow = connections.Single(c => c.OwnerUserId == inviter.Id);
        Assert.Equal("Connected", inviterRow.Status);
        Assert.Equal(acceptor.Id, inviterRow.FriendUserId);
        Assert.NotNull(inviterRow.ConnectedAtUtc);
    }

    // --- ListForUserAsync Tests ---

    [Fact]
    public async Task ListForUser_ReturnsOnlyOwnersConnections()
    {
        await using var ctx = await FriendTestContext.CreateAsync();
        var userA = MakeUser("a@test.com", "UserA");
        var userB = MakeUser("b@test.com", "UserB");
        ctx.Db.Users.AddRange(userA, userB);
        await ctx.Db.SaveChangesAsync();

        // Create connections for both users
        await ctx.FriendService.UpsertOnInviteAcceptedAsync(userA.Id, userB.Id, CancellationToken.None);

        var listA = await ctx.FriendService.ListForUserAsync(userA.Id, CancellationToken.None);
        Assert.Single(listA);
        Assert.Equal("UserB", listA[0].DisplayName);

        var listB = await ctx.FriendService.ListForUserAsync(userB.Id, CancellationToken.None);
        Assert.Single(listB);
        Assert.Equal("UserA", listB[0].DisplayName);
    }

    [Fact]
    public async Task ListForUser_SortsConnectedBeforePending()
    {
        await using var ctx = await FriendTestContext.CreateAsync();
        var owner = MakeUser("owner@test.com", "Owner");
        var friend = MakeUser("friend@test.com", "Friend");
        ctx.Db.Users.AddRange(owner, friend);
        await ctx.Db.SaveChangesAsync();

        // Create a Pending connection
        await ctx.FriendService.UpsertOnInviteSentAsync(
            owner.Id, "pending@unknown.com", "Pending Person", CancellationToken.None);

        // Create a Connected connection
        await ctx.FriendService.UpsertOnInviteAcceptedAsync(
            owner.Id, friend.Id, CancellationToken.None);

        var list = await ctx.FriendService.ListForUserAsync(owner.Id, CancellationToken.None);
        Assert.Equal(2, list.Count);
        Assert.Equal("Connected", list[0].Status);
        Assert.Equal("Pending", list[1].Status);
    }

    [Fact]
    public async Task ListForUser_ReturnsEmptyWhenNoConnections()
    {
        await using var ctx = await FriendTestContext.CreateAsync();
        var owner = MakeUser("owner@test.com", "Owner");
        ctx.Db.Users.Add(owner);
        await ctx.Db.SaveChangesAsync();

        var list = await ctx.FriendService.ListForUserAsync(owner.Id, CancellationToken.None);
        Assert.Empty(list);
    }

    // --- Balance Computation Tests ---

    [Fact]
    public async Task ListForUser_ComputesZeroBalanceWhenNoExpenses()
    {
        await using var ctx = await FriendTestContext.CreateAsync();
        var userA = MakeUser("a@test.com", "UserA");
        var userB = MakeUser("b@test.com", "UserB");
        ctx.Db.Users.AddRange(userA, userB);
        var group = MakeGroup(userA.Id);
        ctx.Db.Groups.Add(group);
        ctx.Db.GroupMembers.AddRange(
            MakeMember(group.Id, userA.Id, Role.OWNER),
            MakeMember(group.Id, userB.Id));
        await ctx.Db.SaveChangesAsync();

        await ctx.FriendService.UpsertOnInviteAcceptedAsync(userA.Id, userB.Id, CancellationToken.None);

        var list = await ctx.FriendService.ListForUserAsync(userA.Id, CancellationToken.None);
        Assert.Single(list);
        Assert.Equal(0, list[0].NetBalanceCents);
        Assert.Equal("All settled", list[0].NetBalanceLabel);
        Assert.Equal(1, list[0].SharedGroupCount);
        Assert.False(list[0].HasActiveSharedBalances);
    }

    [Fact]
    public async Task ListForUser_ComputesPositiveBalance_WhenFriendOwes()
    {
        await using var ctx = await FriendTestContext.CreateAsync();
        var userA = MakeUser("a@test.com", "UserA");
        var userB = MakeUser("b@test.com", "UserB");
        ctx.Db.Users.AddRange(userA, userB);
        var group = MakeGroup(userA.Id);
        ctx.Db.Groups.Add(group);
        ctx.Db.GroupMembers.AddRange(
            MakeMember(group.Id, userA.Id, Role.OWNER),
            MakeMember(group.Id, userB.Id));

        // UserA paid $20, split equally ($10 each)
        var expense = new ExpenseEntity
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            PayerUserId = userA.Id,
            CreatedByUserId = userA.Id,
            Description = "Dinner",
            AmountCents = 2000,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        ctx.Db.Expenses.Add(expense);
        ctx.Db.ExpenseSplits.AddRange(
            new ExpenseSplitEntity { Id = Guid.NewGuid(), ExpenseId = expense.Id, UserId = userA.Id, AmountOwedCents = 1000 },
            new ExpenseSplitEntity { Id = Guid.NewGuid(), ExpenseId = expense.Id, UserId = userB.Id, AmountOwedCents = 1000 });
        await ctx.Db.SaveChangesAsync();

        await ctx.FriendService.UpsertOnInviteAcceptedAsync(userA.Id, userB.Id, CancellationToken.None);

        var list = await ctx.FriendService.ListForUserAsync(userA.Id, CancellationToken.None);
        Assert.Single(list);
        Assert.Equal(1000, list[0].NetBalanceCents);
        Assert.Equal("UserB owes you $10.00", list[0].NetBalanceLabel);
        Assert.True(list[0].HasActiveSharedBalances);
    }

    [Fact]
    public async Task ListForUser_ComputesNegativeBalance_WhenOwnerOwes()
    {
        await using var ctx = await FriendTestContext.CreateAsync();
        var userA = MakeUser("a@test.com", "UserA");
        var userB = MakeUser("b@test.com", "UserB");
        ctx.Db.Users.AddRange(userA, userB);
        var group = MakeGroup(userA.Id);
        ctx.Db.Groups.Add(group);
        ctx.Db.GroupMembers.AddRange(
            MakeMember(group.Id, userA.Id, Role.OWNER),
            MakeMember(group.Id, userB.Id));

        // UserB paid $30, split equally ($15 each)
        var expense = new ExpenseEntity
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            PayerUserId = userB.Id,
            CreatedByUserId = userB.Id,
            Description = "Lunch",
            AmountCents = 3000,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        ctx.Db.Expenses.Add(expense);
        ctx.Db.ExpenseSplits.AddRange(
            new ExpenseSplitEntity { Id = Guid.NewGuid(), ExpenseId = expense.Id, UserId = userA.Id, AmountOwedCents = 1500 },
            new ExpenseSplitEntity { Id = Guid.NewGuid(), ExpenseId = expense.Id, UserId = userB.Id, AmountOwedCents = 1500 });
        await ctx.Db.SaveChangesAsync();

        await ctx.FriendService.UpsertOnInviteAcceptedAsync(userA.Id, userB.Id, CancellationToken.None);

        var list = await ctx.FriendService.ListForUserAsync(userA.Id, CancellationToken.None);
        Assert.Single(list);
        Assert.Equal(-1500, list[0].NetBalanceCents);
        Assert.Equal("You owe UserB $15.00", list[0].NetBalanceLabel);
    }

    [Fact]
    public async Task ListForUser_CrossGroupNetting_CombinesMultipleGroups()
    {
        await using var ctx = await FriendTestContext.CreateAsync();
        var userA = MakeUser("a@test.com", "UserA");
        var userB = MakeUser("b@test.com", "UserB");
        ctx.Db.Users.AddRange(userA, userB);

        var group1 = MakeGroup(userA.Id, "Group 1");
        var group2 = MakeGroup(userA.Id, "Group 2");
        ctx.Db.Groups.AddRange(group1, group2);
        ctx.Db.GroupMembers.AddRange(
            MakeMember(group1.Id, userA.Id, Role.OWNER),
            MakeMember(group1.Id, userB.Id),
            MakeMember(group2.Id, userA.Id, Role.OWNER),
            MakeMember(group2.Id, userB.Id));

        // Group 1: A paid $20, split equally → B owes A $10
        var exp1 = new ExpenseEntity
        {
            Id = Guid.NewGuid(), GroupId = group1.Id, PayerUserId = userA.Id,
            CreatedByUserId = userA.Id, Description = "G1", AmountCents = 2000,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        ctx.Db.Expenses.Add(exp1);
        ctx.Db.ExpenseSplits.AddRange(
            new ExpenseSplitEntity { Id = Guid.NewGuid(), ExpenseId = exp1.Id, UserId = userA.Id, AmountOwedCents = 1000 },
            new ExpenseSplitEntity { Id = Guid.NewGuid(), ExpenseId = exp1.Id, UserId = userB.Id, AmountOwedCents = 1000 });

        // Group 2: B paid $40, split equally → A owes B $20
        var exp2 = new ExpenseEntity
        {
            Id = Guid.NewGuid(), GroupId = group2.Id, PayerUserId = userB.Id,
            CreatedByUserId = userB.Id, Description = "G2", AmountCents = 4000,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        ctx.Db.Expenses.Add(exp2);
        ctx.Db.ExpenseSplits.AddRange(
            new ExpenseSplitEntity { Id = Guid.NewGuid(), ExpenseId = exp2.Id, UserId = userA.Id, AmountOwedCents = 2000 },
            new ExpenseSplitEntity { Id = Guid.NewGuid(), ExpenseId = exp2.Id, UserId = userB.Id, AmountOwedCents = 2000 });
        await ctx.Db.SaveChangesAsync();

        await ctx.FriendService.UpsertOnInviteAcceptedAsync(userA.Id, userB.Id, CancellationToken.None);

        var list = await ctx.FriendService.ListForUserAsync(userA.Id, CancellationToken.None);
        Assert.Single(list);
        // Net: +1000 (group1) - 2000 (group2) = -1000
        Assert.Equal(-1000, list[0].NetBalanceCents);
        Assert.Equal("You owe UserB $10.00", list[0].NetBalanceLabel);
        Assert.Equal(2, list[0].SharedGroupCount);
    }

    [Fact]
    public async Task ListForUser_SettlementReducesBalance()
    {
        await using var ctx = await FriendTestContext.CreateAsync();
        var userA = MakeUser("a@test.com", "UserA");
        var userB = MakeUser("b@test.com", "UserB");
        ctx.Db.Users.AddRange(userA, userB);
        var group = MakeGroup(userA.Id);
        ctx.Db.Groups.Add(group);
        ctx.Db.GroupMembers.AddRange(
            MakeMember(group.Id, userA.Id, Role.OWNER),
            MakeMember(group.Id, userB.Id));

        // A paid $20, split equally → B owes A $10
        var expense = new ExpenseEntity
        {
            Id = Guid.NewGuid(), GroupId = group.Id, PayerUserId = userA.Id,
            CreatedByUserId = userA.Id, Description = "Dinner", AmountCents = 2000,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        ctx.Db.Expenses.Add(expense);
        ctx.Db.ExpenseSplits.AddRange(
            new ExpenseSplitEntity { Id = Guid.NewGuid(), ExpenseId = expense.Id, UserId = userA.Id, AmountOwedCents = 1000 },
            new ExpenseSplitEntity { Id = Guid.NewGuid(), ExpenseId = expense.Id, UserId = userB.Id, AmountOwedCents = 1000 });

        // Settlement: B paid A $10 → cancels out
        ctx.Db.Settlements.Add(new SettlementEntity
        {
            Id = Guid.NewGuid(), GroupId = group.Id,
            FromUserId = userB.Id, ToUserId = userA.Id,
            AmountCents = 1000, CreatedAt = DateTimeOffset.UtcNow,
        });
        await ctx.Db.SaveChangesAsync();

        await ctx.FriendService.UpsertOnInviteAcceptedAsync(userA.Id, userB.Id, CancellationToken.None);

        var list = await ctx.FriendService.ListForUserAsync(userA.Id, CancellationToken.None);
        Assert.Single(list);
        Assert.Equal(0, list[0].NetBalanceCents);
        Assert.Equal("All settled", list[0].NetBalanceLabel);
    }

    // --- GetDetailAsync Tests ---

    [Fact]
    public async Task GetDetail_ReturnsPerGroupBreakdown()
    {
        await using var ctx = await FriendTestContext.CreateAsync();
        var userA = MakeUser("a@test.com", "UserA");
        var userB = MakeUser("b@test.com", "UserB");
        ctx.Db.Users.AddRange(userA, userB);

        var group1 = MakeGroup(userA.Id, "Trips");
        var group2 = MakeGroup(userA.Id, "Rent");
        ctx.Db.Groups.AddRange(group1, group2);
        ctx.Db.GroupMembers.AddRange(
            MakeMember(group1.Id, userA.Id, Role.OWNER),
            MakeMember(group1.Id, userB.Id),
            MakeMember(group2.Id, userA.Id, Role.OWNER),
            MakeMember(group2.Id, userB.Id));

        var exp1 = new ExpenseEntity
        {
            Id = Guid.NewGuid(), GroupId = group1.Id, PayerUserId = userA.Id,
            CreatedByUserId = userA.Id, Description = "Trip", AmountCents = 5000,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        ctx.Db.Expenses.Add(exp1);
        ctx.Db.ExpenseSplits.AddRange(
            new ExpenseSplitEntity { Id = Guid.NewGuid(), ExpenseId = exp1.Id, UserId = userA.Id, AmountOwedCents = 2500 },
            new ExpenseSplitEntity { Id = Guid.NewGuid(), ExpenseId = exp1.Id, UserId = userB.Id, AmountOwedCents = 2500 });
        await ctx.Db.SaveChangesAsync();

        await ctx.FriendService.UpsertOnInviteAcceptedAsync(userA.Id, userB.Id, CancellationToken.None);

        var fc = await ctx.Db.FriendConnections.FirstAsync(c => c.OwnerUserId == userA.Id);
        var detail = await ctx.FriendService.GetDetailAsync(userA.Id, fc.Id, CancellationToken.None);

        Assert.Equal("UserB", detail.DisplayName);
        Assert.Equal(2500, detail.NetBalanceCents);
        Assert.Equal("UserB owes you $25.00", detail.NetBalanceLabel);
        Assert.Equal(2, detail.SharedGroups.Count);
    }

    [Fact]
    public async Task GetDetail_ThrowsNotFound_WhenWrongOwner()
    {
        await using var ctx = await FriendTestContext.CreateAsync();
        var userA = MakeUser("a@test.com", "UserA");
        var userB = MakeUser("b@test.com", "UserB");
        var userC = MakeUser("c@test.com", "UserC");
        ctx.Db.Users.AddRange(userA, userB, userC);
        await ctx.Db.SaveChangesAsync();

        await ctx.FriendService.UpsertOnInviteAcceptedAsync(userA.Id, userB.Id, CancellationToken.None);

        var fc = await ctx.Db.FriendConnections.FirstAsync(c => c.OwnerUserId == userA.Id);

        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            ctx.FriendService.GetDetailAsync(userC.Id, fc.Id, CancellationToken.None));
    }

    [Fact]
    public async Task GetDetail_ThrowsNotFound_ForNonexistentId()
    {
        await using var ctx = await FriendTestContext.CreateAsync();
        var userA = MakeUser("a@test.com", "UserA");
        ctx.Db.Users.Add(userA);
        await ctx.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            ctx.FriendService.GetDetailAsync(userA.Id, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task GetDetail_ReturnsEmptySharedGroups_WhenNoneShared()
    {
        await using var ctx = await FriendTestContext.CreateAsync();
        var userA = MakeUser("a@test.com", "UserA");
        var userB = MakeUser("b@test.com", "UserB");
        ctx.Db.Users.AddRange(userA, userB);
        await ctx.Db.SaveChangesAsync();

        await ctx.FriendService.UpsertOnInviteAcceptedAsync(userA.Id, userB.Id, CancellationToken.None);

        var fc = await ctx.Db.FriendConnections.FirstAsync(c => c.OwnerUserId == userA.Id);
        var detail = await ctx.FriendService.GetDetailAsync(userA.Id, fc.Id, CancellationToken.None);

        Assert.Empty(detail.SharedGroups);
        Assert.Equal(0, detail.NetBalanceCents);
        Assert.Equal("All settled", detail.Summary);
    }
}
