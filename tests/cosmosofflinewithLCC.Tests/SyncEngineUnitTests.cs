using Moq;
using cosmosofflinewithLCC.Data;
using cosmosofflinewithLCC.Models;
using cosmosofflinewithLCC.Sync;
using Microsoft.Extensions.Logging;

namespace cosmosofflinewithLCC.Tests
{

    public class SyncEngineUnitTests
    {
        private readonly Mock<ILogger> _loggerMock = new Mock<ILogger>();

        [Fact]
        public async Task SyncAsync_PushesLocalPendingChangeToRemote_WhenRemoteDoesNotHaveItem()
        {
            // Arrange
            var userId = "user1";
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var pendingItem = new Item { ID = "1", Content = "A", LastModified = now, OIID = userId, Type = "Item" };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item> { pendingItem });
            remoteMock.Setup(x => x.GetAsync("1", userId)).ReturnsAsync((Item?)null);
            remoteMock.Setup(x => x.GetByUserIdAsync(userId)).ReturnsAsync(new List<Item> { pendingItem });

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, userId);

            // Act
            await syncEngine.SyncAsync();            // Assert
            remoteMock.Verify(x => x.UpsertBulkAsync(
                It.Is<IEnumerable<Item>>(items =>
                    items.Any(i => i.ID == pendingItem.ID &&
                            i.Content == pendingItem.Content &&
                            i.LastModified == now &&
                            i.OIID == userId &&
                            i.Type == "Item" &&
                            i.PartitionKey == $"{userId}:Item")),
                It.IsAny<bool>()),
                Times.Once);
            localMock.Verify(x => x.RemovePendingChangeAsync("1"), Times.Once);
            localMock.Verify(x => x.GetPendingChangesAsync(), Times.Once);
            remoteMock.Verify(x => x.GetAsync("1", userId), Times.Once);
        }

        [Fact]
        public async Task SyncAsync_PushesLocalPendingChangeToRemote_WhenLocalIsNewerThanRemote()
        {
            // Arrange
            var userId = "user1";
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var pendingItem = new Item { ID = "2", Content = "B", LastModified = now, OIID = userId, Type = "Item" };
            var remoteItem = new Item { ID = "2", Content = "Old", LastModified = now.AddMinutes(-10), OIID = userId, Type = "Item" };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item> { pendingItem });
            remoteMock.Setup(x => x.GetAsync("2", userId)).ReturnsAsync(remoteItem);
            remoteMock.Setup(x => x.GetByUserIdAsync(userId)).ReturnsAsync(new List<Item>()); // Prevent the pull logic from affecting the test

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, userId);

            // Act
            await syncEngine.SyncAsync();

            // Assert
            remoteMock.Verify(x => x.UpsertBulkAsync(
                It.Is<IEnumerable<Item>>(items =>
                    items.Any(i => i.ID == pendingItem.ID &&
                            i.Content == pendingItem.Content &&
                            i.LastModified == now &&
                            i.OIID == userId &&
                            i.Type == "Item" &&
                            i.PartitionKey == $"{userId}:Item")), true), Times.Once);
            localMock.Verify(x => x.RemovePendingChangeAsync("2"), Times.Once);
        }

        [Fact]
        public async Task SyncAsync_DoesNotPushLocalPendingChange_WhenRemoteIsNewer()
        {
            // Arrange
            var userId = "user1";
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var pendingItem = new Item { ID = "3", Content = "C", LastModified = now.AddMinutes(-2), OIID = userId, Type = "Item" };
            var remoteItem = new Item { ID = "3", Content = "Newer", LastModified = now, OIID = userId, Type = "Item" };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item> { pendingItem });
            remoteMock.Setup(x => x.GetAsync("3", userId)).ReturnsAsync(remoteItem);
            remoteMock.Setup(x => x.GetByUserIdAsync(userId)).ReturnsAsync(new List<Item> { remoteItem });

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, userId);

            // Act
            await syncEngine.SyncAsync();

            // Assert
            remoteMock.Verify(x => x.UpsertBulkAsync(It.IsAny<IEnumerable<Item>>()), Times.Never);
            localMock.Verify(x => x.RemovePendingChangeAsync("3"), Times.Once);
        }

        [Fact]
        public async Task SyncAsync_PullsRemoteItemToLocal_WhenRemoteIsNewerThanLocal()
        {
            // Arrange
            var userId = "user1";
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var remoteItem = new Item { ID = "4", Content = "Remote", LastModified = now, OIID = userId, Type = "Item" };
            var localItem = new Item { ID = "4", Content = "Old", LastModified = now.AddMinutes(-1), OIID = userId, Type = "Item" };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item>());
            remoteMock.Setup(x => x.GetByUserIdAsync(userId)).ReturnsAsync(new List<Item> { remoteItem });
            localMock.Setup(x => x.GetAsync("4", userId)).ReturnsAsync(localItem);

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, userId);

            // Act
            await syncEngine.SyncAsync();

            // Assert
            localMock.Verify(x => x.UpsertBulkAsync(
                It.Is<IEnumerable<Item>>(items =>
                    items.Any(i => i.ID == remoteItem.ID &&
                            i.Content == remoteItem.Content &&
                            i.LastModified == now &&
                            i.OIID == userId &&
                            i.Type == "Item" &&
                            i.PartitionKey == $"{userId}:Item")),
                false), Times.Once);
        }

        [Fact]
        public async Task SyncAsync_PullsRemoteItemToLocal_WhenLocalDoesNotHaveItem()
        {
            // Arrange
            var userId = "user1";
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var remoteItem = new Item { ID = "5", Content = "RemoteOnly", LastModified = now, OIID = userId, Type = "Item" };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item>());
            remoteMock.Setup(x => x.GetByUserIdAsync(userId)).ReturnsAsync(new List<Item> { remoteItem });
            localMock.Setup(x => x.GetAsync("5", userId)).ReturnsAsync((Item?)null);

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, userId);

            // Act
            await syncEngine.SyncAsync();

            // Assert
            localMock.Verify(x => x.UpsertBulkAsync(
                It.Is<IEnumerable<Item>>(items =>
                    items.Any(i => i.ID == remoteItem.ID &&
                            i.Content == remoteItem.Content &&
                            i.LastModified == now &&
                            i.OIID == userId &&
                            i.Type == "Item" &&
                            i.PartitionKey == $"{userId}:Item")), false), Times.Once);
        }

        [Fact]
        public async Task SyncAsync_DoesNotPullRemoteItem_WhenLocalIsNewer()
        {
            // Arrange
            var userId = "user1";
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var remoteItem = new Item { ID = "6", Content = "OldRemote", LastModified = now.AddMinutes(-2), OIID = userId, Type = "Item" };
            var localItem = new Item { ID = "6", Content = "NewerLocal", LastModified = now, OIID = userId, Type = "Item" };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item>());
            remoteMock.Setup(x => x.GetByUserIdAsync(userId)).ReturnsAsync(new List<Item> { remoteItem });
            localMock.Setup(x => x.GetAsync("6", userId)).ReturnsAsync(localItem);

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, userId);

            // Act
            await syncEngine.SyncAsync();

            // Assert
            localMock.Verify(x => x.UpsertBulkAsync(It.IsAny<IEnumerable<Item>>()), Times.Never);
        }

        [Fact]
        public async Task SyncAsync_FiltersByUserId_WhenUserIdIsProvided()
        {
            // Arrange
            var userId = "user1";
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var remoteItem = new Item { ID = "7", Content = "UserSpecific", LastModified = now, OIID = userId, Type = "Item" };
            remoteMock.Setup(x => x.GetByUserIdAsync(userId)).ReturnsAsync(new List<Item> { remoteItem });
            localMock.Setup(x => x.GetAsync("7", userId)).ReturnsAsync((Item?)null);
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item>());

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, userId);

            // Act
            await syncEngine.SyncAsync();

            // Assert
            localMock.Verify(x => x.UpsertBulkAsync(
                It.Is<IEnumerable<Item>>(items =>
                    items.Any(i => i.ID == "7" && i.OIID == userId && i.Type == "Item")),
                false), Times.Once);
        }

        [Fact]
        public async Task InitialUserDataPull_ShouldPassTypeToEnsureProperties()
        {
            // Arrange
            var userId = "user1";
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var userItem = new Item { ID = "9", Content = "TypeTest", LastModified = now, OIID = userId, Type = "CustomType" };
            var userItemNoType = new Item { ID = "10", Content = "NoTypeTest", LastModified = now, OIID = userId, Type = null! };

            remoteMock.Setup(x => x.GetByUserIdAsync(userId)).ReturnsAsync(new List<Item> { userItem, userItemNoType });
            localMock.Setup(x => x.GetAsync("9", userId)).ReturnsAsync((Item?)null);
            localMock.Setup(x => x.GetAsync("10", userId)).ReturnsAsync((Item?)null);

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, userId);

            // Act
            await syncEngine.InitialUserDataPullAsync("SpecifiedType");

            // Assert
            localMock.Verify(x => x.UpsertBulkAsync(It.IsAny<IEnumerable<Item>>(), false), Times.Once);
        }

        [Fact]
        public async Task SyncAsync_ShouldEnsureCosmosProperties_WhenRemoteIsIDocumentStore()
        {
            // Arrange
            var userId = "user1";
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var pendingItem = new Item { ID = "11", Content = "NoType", LastModified = now, OIID = userId, Type = null! };

            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item> { pendingItem });
            remoteMock.Setup(x => x.GetAsync("11", userId)).ReturnsAsync((Item?)null);
            remoteMock.Setup(x => x.GetByUserIdAsync(userId)).ReturnsAsync(new List<Item>());

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, userId);

            // Act
            await syncEngine.SyncAsync();

            // Assert
            remoteMock.Verify(x => x.UpsertBulkAsync(It.IsAny<IEnumerable<Item>>(), true), Times.Once);
        }

        [Fact]
        public async Task SyncAsync_ShouldRespectDocumentType_WhenUsingCompositePartitionKey()
        {
            // Arrange
            var userId = "user1";
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = DateTime.UtcNow;
            var localItem = new Item
            {
                ID = "type-test",
                Content = "Type-specific item",
                LastModified = now,
                OIID = userId,
                Type = "CustomType"
            };

            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item> { localItem });
            remoteMock.Setup(x => x.GetAsync("type-test", userId)).ReturnsAsync((Item?)null);
            remoteMock.Setup(x => x.GetByUserIdAsync(userId)).ReturnsAsync(new List<Item>());

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, userId);

            // Act
            await syncEngine.SyncAsync();

            // Assert
            remoteMock.Verify(x => x.UpsertBulkAsync(It.Is<IEnumerable<Item>>(items =>
                items.Any(i => i.ID == "type-test" &&
                       i.OIID == userId &&
                       i.Type == "CustomType")), true), Times.Once);
        }

        [Fact]
        public async Task InitialDataPull_ShouldUseProvidedDocType_WhenItemsHaveNoType()
        {
            // Arrange
            var userId = "user1";
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = DateTime.UtcNow;
            var remoteItem = new Item
            {
                ID = "typeless",
                Content = "No type specified",
                LastModified = now,
                OIID = userId,
                Type = null! // Type will be null
            };

            remoteMock.Setup(x => x.GetByUserIdAsync(userId)).ReturnsAsync(new List<Item> { remoteItem });
            localMock.Setup(x => x.GetAsync("typeless", userId)).ReturnsAsync((Item?)null);

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, userId);

            // Act
            await syncEngine.InitialUserDataPullAsync("SpecifiedType");

            // Assert
            localMock.Verify(x => x.UpsertBulkAsync(It.Is<IEnumerable<Item>>(items =>
                items.Any(i => i.ID == "typeless" &&
                       i.OIID == userId)), false), Times.Once);
        }
        [Fact]
        public async Task UpdateUserId_UpdatesUserIdSuccessfully()
        {            // Arrange
            var initialUserId = "user1";
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = DateTime.UtcNow;
            var newUserId = "user2";
            var item = new Item { ID = "1", Content = "Test", LastModified = now, OIID = newUserId };

            // Setup empty pending changes to avoid null reference
            localMock.Setup(x => x.GetPendingChangesAsync())
                    .ReturnsAsync(new List<Item>());

            remoteMock.Setup(x => x.GetByUserIdAsync(newUserId))
                     .ReturnsAsync(new List<Item> { item })
                     .Verifiable();

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, initialUserId);

            // Act
            syncEngine.UpdateUserId(newUserId);
            await syncEngine.SyncAsync();

            // Assert
            remoteMock.Verify(x => x.GetByUserIdAsync(newUserId), Times.Once);
            remoteMock.Verify(x => x.GetByUserIdAsync(initialUserId), Times.Never);
            remoteMock.VerifyAll();
        }

        [Fact]
        public void UpdateUserId_ThrowsException_WhenUserIdIsEmpty()
        {
            // Arrange
            var userId = "user1";
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, userId);

            // Act & Assert
            var emptyEx = Assert.Throws<ArgumentException>(() => syncEngine.UpdateUserId(string.Empty));
            Assert.Equal("userId must not be null or empty (Parameter 'userId')", emptyEx.Message);

            var whitespaceEx = Assert.Throws<ArgumentException>(() => syncEngine.UpdateUserId("   "));
            Assert.Equal("userId must not be null or empty (Parameter 'userId')", whitespaceEx.Message);
        }

        [Fact]
        public async Task InitialUserDataPullAsync_DoesNotMarkItemsAsPending()
        {
            // Arrange
            var userId = "user1";
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var remoteItems = new List<Item>
            {
                new Item { ID = "1", Content = "Remote1", LastModified = now, OIID = userId, Type = "Item" },
                new Item { ID = "2", Content = "Remote2", LastModified = now, OIID = userId, Type = "Item" }
            };

            remoteMock.Setup(x => x.GetByUserIdAsync(userId)).ReturnsAsync(remoteItems);
            localMock.Setup(x => x.GetAsync(It.IsAny<string>(), userId)).ReturnsAsync((Item?)null);

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, userId);

            // Act
            await syncEngine.InitialUserDataPullAsync("Item");

            // Assert
            localMock.Verify(x => x.UpsertBulkAsync(
                It.Is<IEnumerable<Item>>(items => items.Count() == 2),
                false), Times.Once);
            localMock.Verify(x => x.RemovePendingChangeAsync(It.IsAny<string>()), Times.Never);
        }
    }
}
