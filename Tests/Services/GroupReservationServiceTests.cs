using APIHandler.Constants;
using APIHandler.Constants.Orders;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Models.GroupReservation;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Orders;
using APIHandler.Repositories.Reservations;
using APIHandler.Repositories.Tables;
using APIHandler.Services.Auth;
using APIHandler.Services.GroupReservations;
using APIHandler.Services.Locations;
using AutoFixture.Xunit2;
using Moq;
using System.Security.Cryptography;

namespace APIHandler.Tests.Services;

public class GroupReservationServiceTests
{
    private readonly Mock<IGroupReservationRepository> _groupReservationRepositoryMock = new();
    private readonly Mock<ITableRepository> _tableRepositoryMock = new();
    private readonly Mock<IReservationRepository> _reservationRepositoryMock = new();
    private readonly Mock<IGuestSessionRepository> _guestSessionRepositoryMock = new();
    private readonly Mock<IOrderRepository> _orderRepositoryMock = new();
    private readonly Mock<IUserService> _userServiceMock = new();
    private readonly Mock<ILocationService> _locationServiceMock = new();

    private readonly GroupReservationService _service;

    public GroupReservationServiceTests()
    {
        _service = new GroupReservationService(
            _groupReservationRepositoryMock.Object,
            _tableRepositoryMock.Object,
            _reservationRepositoryMock.Object,
            _guestSessionRepositoryMock.Object,
            _orderRepositoryMock.Object,
            _userServiceMock.Object,
            _locationServiceMock.Object);
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationByTokenAsync_ValidInput_ReturnsGroupReservationInfo(
        GroupReservation groupReservation,
        Table table,
        Reservation reservation,
        Location location,
        string expectedUserName)
    {
        groupReservation.ReservationId = reservation.Id;
        groupReservation.Status = GroupReservationStatus.Open;
        reservation.TableId = table.Id;
        table.LocationId = location.Id;

        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByTokenAsync(groupReservation.ReservationToken))
            .ReturnsAsync(groupReservation);

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _guestSessionRepositoryMock
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(0);

        _tableRepositoryMock
            .Setup(r => r.GetTableAsync(table.Id))
            .ReturnsAsync(table);

        _locationServiceMock
            .Setup(s => s.GetLocationByIdAsync(table.LocationId))
            .ReturnsAsync(location);

        _userServiceMock
            .Setup(s => s.GetUserName(reservation.CustomerId!))
            .ReturnsAsync(expectedUserName);

        var result = await _service.GetGroupReservationByTokenAsync(groupReservation.ReservationToken);

        Assert.Equal(groupReservation.GroupReservationId, result.GroupReservationId);
        Assert.Equal(table.LocationId, result.LocationId);
        Assert.Equal(reservation.GuestCount, result.MaxGuestCount);
        Assert.Equal(reservation.StartTime, result.StartTime);
        Assert.Equal(reservation.EndTime, result.EndTime);
        Assert.Equal(table.TableNumber, result.TableNumber);
        Assert.Equal("Green & Tasty", result.RestaurantName);
        Assert.Equal(location.Address, result.LocationAddress);
        Assert.Equal(expectedUserName, result.InviterName);
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationByTokenAsync_InvalidToken_ThrowsNotFound(string token)
    {
        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByTokenAsync(token))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(() => _service.GetGroupReservationByTokenAsync(token));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Closed)]
    [InlineAutoData(GroupReservationStatus.Submitted)]
    public async Task GetGroupReservationByTokenAsync_GroupReservationIsNotOpen_ThrowsException(
        GroupReservationStatus status,
        GroupReservation groupReservation)
    {
        groupReservation.Status = status;

        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByTokenAsync(groupReservation.ReservationToken))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<GroupReservationExpiredException>(() => _service.GetGroupReservationByTokenAsync(groupReservation.ReservationToken));
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationByTokenAsync_WhenGuestCountIsMax_ThrowsGuestLimitExceeded(GroupReservation groupReservation, Reservation reservation)
    {
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = reservation.Id;

        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByTokenAsync(groupReservation.ReservationToken))
            .ReturnsAsync(groupReservation);

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _guestSessionRepositoryMock
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(reservation.GuestCount);

        await Assert.ThrowsAsync<GuestLimitExceededException>(() => _service.GetGroupReservationByTokenAsync(groupReservation.ReservationToken));
    }

    [Theory]
    [AutoData]
    public async Task CloseGroupReservationAsync_ValidInput_UpdatesGroupReservationStatus(GroupReservation groupReservation)
    {
        _groupReservationRepositoryMock
            .Setup(r => r.GetByReservationIdAsync(groupReservation.ReservationId))
            .ReturnsAsync(groupReservation);

        GroupReservation? savedGroupReservation = null;
        _groupReservationRepositoryMock
            .Setup(r => r.SaveGroupReservationAsync(It.IsAny<GroupReservation>()))
            .Callback<GroupReservation>(r => savedGroupReservation = r);

        await _service.CloseGroupReservationAsync(groupReservation.ReservationId);

        Assert.NotNull(savedGroupReservation);
        Assert.Equal(GroupReservationStatus.Closed, savedGroupReservation.Status);
    }

    [Theory]
    [AutoData]
    public async Task CloseGroupReservationAsync_GroupReservationNotFound_DoesNotThrowException(string reservationId)
    {
        _groupReservationRepositoryMock
            .Setup(r => r.GetByReservationIdAsync(reservationId))
            .ReturnsAsync((GroupReservation?)null);

        await _service.CloseGroupReservationAsync(reservationId);
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationInviteTokenAsync_ValidInput_ReturnsToken(Reservation reservation, GroupReservation groupReservation)
    {
        reservation.Status = ReservationStatus.Reserved;
        reservation.GuestCount = 3;
        reservation.CustomerId = "owner-1";
        groupReservation.ReservationId = reservation.Id;
        groupReservation.Status = GroupReservationStatus.Open;

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _groupReservationRepositoryMock
            .Setup(r => r.GetByReservationIdAsync(reservation.Id))
            .ReturnsAsync(groupReservation);

        GetGroupInviteTokenResponse result = await _service.GetGroupReservationInviteTokenAsync("owner-1", reservation.Id);

        Assert.Equal(groupReservation.ReservationToken, result.Token);
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationInviteTokenAsync_InvalidReservationId_ThrowsNotFound(string reservationId)
    {
        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservationId))
            .ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<ReservationNotFoundException>(() => _service.GetGroupReservationInviteTokenAsync("owner-1", reservationId));
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationInviteTokenAsync_OneGuest_ThrowsValidationException(Reservation reservation)
    {
        reservation.Status = ReservationStatus.Reserved;
        reservation.GuestCount = 1;
        reservation.CustomerId = "owner-1";

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        await Assert.ThrowsAsync<ValidationException>(() => _service.GetGroupReservationInviteTokenAsync("owner-1", reservation.Id));
    }

    [Theory]
    [InlineAutoData(ReservationStatus.Completed)]
    [InlineAutoData(ReservationStatus.Cancelled)]
    [InlineAutoData(ReservationStatus.InProgress)]
    public async Task GetGroupReservationInviteTokenAsync_StatusIsNotReserved_ThrowsValidationException(ReservationStatus reservationStatus, Reservation reservation)
    {
        reservation.Status = reservationStatus;
        reservation.GuestCount = 1;
        reservation.CustomerId = "owner-1";

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        await Assert.ThrowsAsync<ValidationException>(() => _service.GetGroupReservationInviteTokenAsync("owner-1", reservation.Id));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    [InlineAutoData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    public async Task GetGroupReservationInviteTokenAsync_GroupStatusIsNotOpen_ThrowsException(GroupReservationStatus status, Type type, Reservation reservation, GroupReservation groupReservation)
    {
        reservation.Status = ReservationStatus.Reserved;
        reservation.GuestCount = 3;
        reservation.CustomerId = "owner-1";
        groupReservation.ReservationId = reservation.Id;
        groupReservation.Status = status;

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _groupReservationRepositoryMock
            .Setup(r => r.GetByReservationIdAsync(reservation.Id))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync(type, () => _service.GetGroupReservationInviteTokenAsync("owner-1", reservation.Id));
    }

    [Fact]
    public async Task GetGroupReservationInviteTokenAsync_NonOwnerIsForbidden()
    {
        var reservation = CreateReservation("reservation-1", "owner-1");
        _reservationRepositoryMock
            .Setup(repository => repository.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        await Assert.ThrowsAsync<ActionForbiddenException>(
            () => _service.GetGroupReservationInviteTokenAsync("other-user", reservation.Id));

        _groupReservationRepositoryMock.Verify(
            repository => repository.GetByReservationIdAsync(It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task GetGroupReservationInviteTokenAsync_GroupReservationNotFoundThrowsNotFound()
    {
        var reservation = CreateReservation("reservation-1", "owner-1");
        reservation.Status = ReservationStatus.Reserved;
        reservation.GuestCount = 2;
        _reservationRepositoryMock
            .Setup(repository => repository.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);
        _groupReservationRepositoryMock
            .Setup(repository => repository.GetByReservationIdAsync(reservation.Id))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(
            () => _service.GetGroupReservationInviteTokenAsync("owner-1", reservation.Id));
    }

    [Fact]
    public async Task CreateGroupReservationAsync_CreatesOwnerSessionForReservationOwner()
    {
        var reservation = new Reservation
        {
            Id = "reservation-1",
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = "owner-1",
        };
        GuestSession? savedSession = null;
        _reservationRepositoryMock
            .Setup(repository => repository.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);
        _guestSessionRepositoryMock
            .Setup(repository => repository.SaveAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(session => savedSession = session);

        var group = await _service.CreateGroupReservationAsync(reservation.Id);

        Assert.NotNull(savedSession);
        Assert.Equal(group.GroupReservationId, savedSession.GroupReservationId);
        Assert.Equal("owner-1", savedSession.OwnerUserId);
        Assert.True(savedSession.IsOwnerSession);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_ReturnsOwnerSessionIdForReservationOwner()
    {
        var reservation = CreateReservation("reservation-1", "owner-1");
        var group = CreateGroup("group-1", reservation.Id);
        var ownerSession = CreateSession("owner-session-1", group.GroupReservationId, isOwner: true, ownerUserId: "owner-1");

        _reservationRepositoryMock.Setup(repository => repository.GetReservationByIdAsync(reservation.Id)).ReturnsAsync(reservation);
        _groupReservationRepositoryMock.Setup(repository => repository.GetByReservationIdAsync(reservation.Id)).ReturnsAsync(group);
        _guestSessionRepositoryMock
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(group.GroupReservationId))
            .ReturnsAsync([CreateSession("guest-session", group.GroupReservationId, isOwner: false), ownerSession]);

        var result = await _service.GetOwnerGuestSessionIdAsync("owner-1", reservation.Id);

        Assert.Equal("owner-session-1", result.GuestSessionId);
        byte[] tokenBytes = Convert.FromBase64String(result.GuestSessionToken);
        Assert.Equal(
            Convert.ToBase64String(SHA256.HashData(tokenBytes)),
            ownerSession.SessionTokenHash);
        _guestSessionRepositoryMock.Verify(
            repository => repository.SaveAsync(ownerSession),
            Times.Once);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_NonOwnerIsForbidden()
    {
        var reservation = CreateReservation("reservation-1", "owner-1");
        _reservationRepositoryMock.Setup(repository => repository.GetReservationByIdAsync(reservation.Id)).ReturnsAsync(reservation);

        await Assert.ThrowsAsync<ActionForbiddenException>(
            () => _service.GetOwnerGuestSessionIdAsync("other-user", reservation.Id));

        _groupReservationRepositoryMock.Verify(
            repository => repository.GetByReservationIdAsync(It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_ReservationNotFoundThrowsNotFound()
    {
        _reservationRepositoryMock
            .Setup(repository => repository.GetReservationByIdAsync("missing-reservation"))
            .ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<ReservationNotFoundException>(
            () => _service.GetOwnerGuestSessionIdAsync("owner-1", "missing-reservation"));
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_GroupReservationNotFoundThrowsNotFound()
    {
        var reservation = CreateReservation("reservation-1", "owner-1");
        _reservationRepositoryMock.Setup(repository => repository.GetReservationByIdAsync(reservation.Id)).ReturnsAsync(reservation);
        _groupReservationRepositoryMock
            .Setup(repository => repository.GetByReservationIdAsync(reservation.Id))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(
            () => _service.GetOwnerGuestSessionIdAsync("owner-1", reservation.Id));
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_MergesSubmittedSessionsAndUpdatesOrderAndGroup()
    {
        var reservation = CreateReservation("reservation-1", "owner-1");
        var group = CreateGroup("group-1", reservation.Id);
        var sessions = new List<GuestSession>
        {
            CreateSession("owner", group.GroupReservationId, isOwner: true, ownerUserId: "owner-1",
                isSubmitted: true, dishes: [new OrderItem { DishId = "dish-1", Quantity = 1 }]),
            CreateSession("guest-1", group.GroupReservationId, isOwner: false,
                isSubmitted: true, dishes: [new OrderItem { DishId = "dish-1", Quantity = 2 }, new OrderItem { DishId = "dish-2", Quantity = 1 }]),
            CreateSession("guest-2", group.GroupReservationId, isOwner: false,
                isSubmitted: false, dishes: [new OrderItem { DishId = "dish-3", Quantity = 20 }]),
        };
        var order = new Order
        {
            OrderId = "order-1",
            CustomerId = "owner-1",
            ReservationId = reservation.Id,
            ReservationAddress = "Address",
            ReservationTime = "Time",
            Dishes = [],
        };
        _groupReservationRepositoryMock.Setup(repository => repository.GetGroupReservationByIdAsync(group.GroupReservationId)).ReturnsAsync(group);
        _reservationRepositoryMock.Setup(repository => repository.GetReservationByIdAsync(reservation.Id)).ReturnsAsync(reservation);
        _guestSessionRepositoryMock.Setup(repository => repository.GetAllByGroupReservationIdAsync(group.GroupReservationId)).ReturnsAsync(sessions);
        _orderRepositoryMock.Setup(repository => repository.GetOrderByReservationIdAsync(reservation.Id)).ReturnsAsync(order);

        Order? savedOrder = null;
        GroupReservation? savedGroup = null;
        _orderRepositoryMock
            .Setup(repository => repository.SaveOrderAsync(It.IsAny<Order>()))
            .Callback<Order>(value => savedOrder = value);
        _groupReservationRepositoryMock
            .Setup(repository => repository.SaveGroupReservationAsync(It.IsAny<GroupReservation>()))
            .Callback<GroupReservation>(value => savedGroup = value);

        var result = await _service.SubmitGroupReservationAsync("owner-1", group.GroupReservationId, reservation.Id);

        Assert.Equal(
            new[] { new ConsolidatedOrderItemResponse("dish-1", 3), new ConsolidatedOrderItemResponse("dish-2", 1) },
            result);
        Assert.NotNull(savedOrder);
        Assert.Equal(OrderStatus.SubmittedByCustomer, savedOrder.Status);
        Assert.Equal(new[] { ("dish-1", 3), ("dish-2", 1) }, savedOrder.Dishes.Select(dish => (dish.DishId, dish.Quantity)));
        Assert.All(savedOrder.Dishes, dish => Assert.Equal(OrderItemState.Pending, dish.State));
        Assert.NotNull(savedGroup);
        Assert.Equal(GroupReservationStatus.Submitted, savedGroup.Status);
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_NonOwnerIsForbidden()
    {
        var reservation = CreateReservation("reservation-1", "owner-1");
        var group = CreateGroup("group-1", reservation.Id);
        _groupReservationRepositoryMock.Setup(repository => repository.GetGroupReservationByIdAsync(group.GroupReservationId)).ReturnsAsync(group);
        _reservationRepositoryMock.Setup(repository => repository.GetReservationByIdAsync(reservation.Id)).ReturnsAsync(reservation);

        await Assert.ThrowsAsync<ActionForbiddenException>(
            () => _service.SubmitGroupReservationAsync("other-user", group.GroupReservationId, reservation.Id));

        _guestSessionRepositoryMock.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_AlreadySubmittedGroupThrowsConflict()
    {
        var reservation = CreateReservation("reservation-1", "owner-1");
        var group = CreateGroup("group-1", reservation.Id);
        group.Status = GroupReservationStatus.Submitted;
        _groupReservationRepositoryMock
            .Setup(repository => repository.GetGroupReservationByIdAsync(group.GroupReservationId))
            .ReturnsAsync(group);
        _reservationRepositoryMock
            .Setup(repository => repository.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        await Assert.ThrowsAsync<GroupReservationSubmittedException>(
            () => _service.SubmitGroupReservationAsync("owner-1", group.GroupReservationId, reservation.Id));

        _guestSessionRepositoryMock.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()),
            Times.Never);
        _orderRepositoryMock.Verify(
            repository => repository.SaveOrderAsync(It.IsAny<Order>()),
            Times.Never);
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_GroupReservationNotFoundThrowsNotFound()
    {
        _groupReservationRepositoryMock
            .Setup(repository => repository.GetGroupReservationByIdAsync("missing-group"))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(
            () => _service.SubmitGroupReservationAsync("owner-1", "missing-group", "reservation-1"));
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_ReservationNotFoundThrowsNotFound()
    {
        var group = CreateGroup("group-1", "reservation-1");
        _groupReservationRepositoryMock.Setup(repository => repository.GetGroupReservationByIdAsync(group.GroupReservationId)).ReturnsAsync(group);
        _reservationRepositoryMock
            .Setup(repository => repository.GetReservationByIdAsync("missing-reservation"))
            .ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<ReservationNotFoundException>(
            () => _service.SubmitGroupReservationAsync("owner-1", group.GroupReservationId, "missing-reservation"));
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_NoSubmittedSessionsIsBadRequest()
    {
        var reservation = CreateReservation("reservation-1", "owner-1");
        var group = CreateGroup("group-1", reservation.Id);
        _groupReservationRepositoryMock.Setup(repository => repository.GetGroupReservationByIdAsync(group.GroupReservationId)).ReturnsAsync(group);
        _reservationRepositoryMock.Setup(repository => repository.GetReservationByIdAsync(reservation.Id)).ReturnsAsync(reservation);
        _guestSessionRepositoryMock
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(group.GroupReservationId))
            .ReturnsAsync([CreateSession("guest-1", group.GroupReservationId, isOwner: false, isSubmitted: false)]);

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.SubmitGroupReservationAsync("owner-1", group.GroupReservationId, reservation.Id));
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_NoDishesInSubmittedSessionsIsBadRequest()
    {
        var reservation = CreateReservation("reservation-1", "owner-1");
        var group = CreateGroup("group-1", reservation.Id);
        _groupReservationRepositoryMock.Setup(repository => repository.GetGroupReservationByIdAsync(group.GroupReservationId)).ReturnsAsync(group);
        _reservationRepositoryMock.Setup(repository => repository.GetReservationByIdAsync(reservation.Id)).ReturnsAsync(reservation);
        _guestSessionRepositoryMock
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(group.GroupReservationId))
            .ReturnsAsync([CreateSession("guest-1", group.GroupReservationId, isOwner: false, isSubmitted: true)]);

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.SubmitGroupReservationAsync("owner-1", group.GroupReservationId, reservation.Id));
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_OrderNotFoundIsBadRequest()
    {
        var reservation = CreateReservation("reservation-1", "owner-1");
        var group = CreateGroup("group-1", reservation.Id);
        _groupReservationRepositoryMock.Setup(repository => repository.GetGroupReservationByIdAsync(group.GroupReservationId)).ReturnsAsync(group);
        _reservationRepositoryMock.Setup(repository => repository.GetReservationByIdAsync(reservation.Id)).ReturnsAsync(reservation);
        _guestSessionRepositoryMock
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(group.GroupReservationId))
            .ReturnsAsync([CreateSession("guest-1", group.GroupReservationId, isOwner: false, isSubmitted: true,
                dishes: [new OrderItem { DishId = "dish-1", Quantity = 1 }])]);
        _orderRepositoryMock.Setup(repository => repository.GetOrderByReservationIdAsync(reservation.Id)).ReturnsAsync((Order?)null);

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.SubmitGroupReservationAsync("owner-1", group.GroupReservationId, reservation.Id));
    }

    private static Reservation CreateReservation(string id, string ownerId) => new()
    {
        Id = id,
        TableId = "table-1",
        WaiterId = "waiter-1",
        CustomerId = ownerId,
    };

    private static GroupReservation CreateGroup(string id, string reservationId) => new()
    {
        GroupReservationId = id,
        ReservationId = reservationId,
        ReservationToken = "token",
    };

    private static GuestSession CreateSession(
        string id,
        string groupId,
        bool isOwner,
        string? ownerUserId = null,
        bool isSubmitted = false,
        params OrderItem[] dishes) => new()
    {
        Id = id,
        GroupReservationId = groupId,
        GuestName = isOwner ? "Owner" : "Guest",
        IsOwnerSession = isOwner,
        OwnerUserId = ownerUserId,
        IsSubmitted = isSubmitted,
        Dishes = [.. dishes],
    };
}
