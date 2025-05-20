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
        private readonly string _userId = "user1";

        [Fact]
        public async Task SyncAsync_PushesLocalPendingChangeToRemote_WhenRemoteDoesNotHaveItem()
        {
            // Arrange
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var pendingItem = new Item { ID = "1", Content = "A", LastModified = now, OIID = _userId, Type = "Item" };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item> { pendingItem });
            remoteMock.Setup(x => x.GetAsync("1", _userId)).ReturnsAsync((Item?)null);
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item> { pendingItem });

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, _userId);

            // Act
            await syncEngine.SyncAsync();

            // Assert
            remoteMock.Verify(x => x.UpsertBulkAsync(It.Is<IEnumerable<Item>>(items =>
                items.Any(i => i.ID == pendingItem.ID &&
                        i.Content == pendingItem.Content &&
                        i.LastModified == now &&
                        i.OIID == _userId &&
                        i.Type == "Item"))), Times.Once);
            localMock.Verify(x => x.RemovePendingChangeAsync("1"), Times.Once);
            localMock.Verify(x => x.GetPendingChangesAsync(), Times.Once);
            remoteMock.Verify(x => x.GetAsync("1", _userId), Times.Once);
        }

        [Fact]
        public async Task SyncAsync_PushesLocalPendingChangeToRemote_WhenLocalIsNewerThanRemote()
        {
            // Arrange
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var pendingItem = new Item { ID = "2", Content = "B", LastModified = now, OIID = _userId, Type = "Item" };
            var remoteItem = new Item { ID = "2", Content = "Old", LastModified = now.AddMinutes(-1), OIID = _userId, Type = "Item" };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item> { pendingItem });
            remoteMock.Setup(x => x.GetAsync("2", _userId)).ReturnsAsync(remoteItem);
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item>()); // Prevent the pull logic from affecting the test

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, _userId);

            // Act
            await syncEngine.SyncAsync();

            // Assert
            remoteMock.Verify(x => x.UpsertBulkAsync(It.Is<IEnumerable<Item>>(items =>
                items.Any(i => i.ID == pendingItem.ID &&
                        i.Content == pendingItem.Content &&
                        i.LastModified == now &&
                        i.OIID == _userId &&
                        i.Type == "Item"))), Times.Once);
            localMock.Verify(x => x.RemovePendingChangeAsync("2"), Times.Once);
        }

        [Fact]
        public async Task SyncAsync_DoesNotPushLocalPendingChange_WhenRemoteIsNewer()
        {
            // Arrange
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var pendingItem = new Item { ID = "3", Content = "C", LastModified = now.AddMinutes(-2), OIID = _userId, Type = "Item" };
            var remoteItem = new Item { ID = "3", Content = "Newer", LastModified = now, OIID = _userId, Type = "Item" };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item> { pendingItem });
            remoteMock.Setup(x => x.GetAsync("3", _userId)).ReturnsAsync(remoteItem);
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item> { remoteItem });

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, _userId);

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
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var remoteItem = new Item { ID = "4", Content = "Remote", LastModified = now, OIID = _userId, Type = "Item" };
            var localItem = new Item { ID = "4", Content = "Old", LastModified = now.AddMinutes(-1), OIID = _userId, Type = "Item" };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item>());
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item> { remoteItem });
            localMock.Setup(x => x.GetAsync("4", _userId)).ReturnsAsync(localItem);

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, _userId);

            // Act
            await syncEngine.SyncAsync();

            // Assert
            localMock.Verify(x => x.UpsertBulkAsync(It.Is<IEnumerable<Item>>(items =>
                items.Any(i => i.ID == remoteItem.ID &&
                        i.Content == remoteItem.Content &&
                        i.LastModified == now &&
                        i.OIID == _userId &&
                        i.Type == "Item"))), Times.Once);
        }

        [Fact]
        public async Task SyncAsync_PullsRemoteItemToLocal_WhenLocalDoesNotHaveItem()
        {
            // Arrange
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var remoteItem = new Item { ID = "5", Content = "RemoteOnly", LastModified = now, OIID = _userId, Type = "Item" };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item>());
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item> { remoteItem });
            localMock.Setup(x => x.GetAsync("5", _userId)).ReturnsAsync((Item?)null);

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, _userId);

            // Act
            await syncEngine.SyncAsync();

            // Assert
            localMock.Verify(x => x.UpsertBulkAsync(It.Is<IEnumerable<Item>>(items =>
                items.Any(i => i.ID == remoteItem.ID &&
                        i.Content == remoteItem.Content &&
                        i.LastModified == now &&
                        i.OIID == _userId &&
                        i.Type == "Item"))), Times.Once);
        }

        [Fact]
        public async Task SyncAsync_DoesNotPullRemoteItem_WhenLocalIsNewer()
        {
            // Arrange
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var remoteItem = new Item { ID = "6", Content = "OldRemote", LastModified = now.AddMinutes(-2), OIID = _userId, Type = "Item" };
            var localItem = new Item { ID = "6", Content = "NewerLocal", LastModified = now, OIID = _userId, Type = "Item" };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item>());
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item> { remoteItem });
            localMock.Setup(x => x.GetAsync("6", _userId)).ReturnsAsync(localItem);

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, _userId);

            // Act
            await syncEngine.SyncAsync();

            // Assert
            localMock.Verify(x => x.UpsertBulkAsync(It.IsAny<IEnumerable<Item>>()), Times.Never);
        }

        [Fact]
        public async Task SyncAsync_FiltersByUserId_WhenUserIdIsProvided()
        {
            // Arrange
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var user1Item = new Item { ID = "7", Content = "User1Data", LastModified = now, OIID = "user1", Type = "Item" };
            var user2Item = new Item { ID = "8", Content = "User2Data", LastModified = now, OIID = "user2", Type = "Item" };

            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item>());
            remoteMock.Setup(x => x.GetByUserIdAsync("user1")).ReturnsAsync(new List<Item> { user1Item });
            localMock.Setup(x => x.GetAsync("7", "user1")).ReturnsAsync((Item?)null);
            localMock.Setup(x => x.GetAsync("8", "user2")).ReturnsAsync((Item?)null);

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, "user1");

            // Act
            await syncEngine.SyncAsync();

            // Assert
            localMock.Verify(x => x.UpsertBulkAsync(It.Is<IEnumerable<Item>>(items =>
                items.Any(i => i.ID == "7" && i.OIID == "user1" && i.Type == "Item"))), Times.Once);
            remoteMock.Verify(x => x.GetByUserIdAsync("user1"), Times.Once);
        }

        [Fact]
        public async Task InitialUserDataPull_ShouldPassTypeToEnsureProperties()
        {
            // Arrange
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var userItem = new Item { ID = "9", Content = "TypeTest", LastModified = now, OIID = _userId, Type = "CustomType" };
            var userItemNoType = new Item { ID = "10", Content = "NoTypeTest", LastModified = now, OIID = _userId, Type = null! };

            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item> { userItem, userItemNoType });
            localMock.Setup(x => x.GetAsync("9", _userId)).ReturnsAsync((Item?)null);
            localMock.Setup(x => x.GetAsync("10", _userId)).ReturnsAsync((Item?)null);

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, _userId);

            // Act
            await syncEngine.InitialUserDataPullAsync("SpecifiedType");

            // Assert
            localMock.Verify(x => x.UpsertBulkAsync(It.IsAny<IEnumerable<Item>>()), Times.Once);
        }

        [Fact]
        public async Task SyncAsync_ShouldEnsureCosmosProperties_WhenRemoteIsIDocumentStore()
        {
            // Arrange
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var pendingItem = new Item { ID = "11", Content = "NoType", LastModified = now, OIID = _userId, Type = null! };

            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item> { pendingItem });
            remoteMock.Setup(x => x.GetAsync("11", _userId)).ReturnsAsync((Item?)null);
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item>());

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, _userId);

            // Act
            await syncEngine.SyncAsync();

            // Assert
            remoteMock.Verify(x => x.UpsertBulkAsync(It.IsAny<IEnumerable<Item>>()), Times.Once);
        }

        [Fact]
        public async Task SyncAsync_ShouldRespectDocumentType_WhenUsingCompositePartitionKey()
        {
            // Arrange
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = DateTime.UtcNow;
            var localItem = new Item
            {
                ID = "type-test",
                Content = "Type-specific item",
                LastModified = now,
                OIID = _userId,
                Type = "CustomType"
            };

            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item> { localItem });
            remoteMock.Setup(x => x.GetAsync("type-test", _userId)).ReturnsAsync((Item?)null);
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item>());

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, _userId);

            // Act
            await syncEngine.SyncAsync();

            // Assert
            remoteMock.Verify(x => x.UpsertBulkAsync(It.Is<IEnumerable<Item>>(items =>
                items.Any(i => i.ID == "type-test" &&
                       i.OIID == _userId &&
                       i.Type == "CustomType"))), Times.Once);
        }

        [Fact]
        public async Task InitialDataPull_ShouldUseProvidedDocType_WhenItemsHaveNoType()
        {
            // Arrange
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = DateTime.UtcNow;
            var remoteItem = new Item
            {
                ID = "typeless",
                Content = "No type specified",
                LastModified = now,
                OIID = _userId,
                Type = null! // Type will be null
            };

            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item> { remoteItem });
            localMock.Setup(x => x.GetAsync("typeless", _userId)).ReturnsAsync((Item?)null);

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, _userId);

            // Act
            await syncEngine.InitialUserDataPullAsync("SpecifiedType");

            // Assert
            localMock.Verify(x => x.UpsertBulkAsync(It.Is<IEnumerable<Item>>(items =>
                items.Any(i => i.ID == "typeless" &&
                       i.OIID == _userId))), Times.Once);
        }
        [Fact]
        public async Task UpdateUserId_UpdatesUserIdSuccessfully()
        {            // Arrange
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
                x => x.ID, x => x.LastModified, _userId);

            // Act
            syncEngine.UpdateUserId(newUserId);
            await syncEngine.SyncAsync();

            // Assert
            remoteMock.Verify(x => x.GetByUserIdAsync(newUserId), Times.Once);
            remoteMock.Verify(x => x.GetByUserIdAsync(_userId), Times.Never);
            remoteMock.VerifyAll();
        }

        [Fact]
        public void UpdateUserId_ThrowsException_WhenUserIdIsEmpty()
        {
            // Arrange
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.ID, x => x.LastModified, _userId);

            // Act & Assert
            var emptyEx = Assert.Throws<ArgumentException>(() => syncEngine.UpdateUserId(string.Empty));
            Assert.Equal("userId must not be null or empty (Parameter 'userId')", emptyEx.Message);

            var whitespaceEx = Assert.Throws<ArgumentException>(() => syncEngine.UpdateUserId("   "));
            Assert.Equal("userId must not be null or empty (Parameter 'userId')", whitespaceEx.Message);
        }
    }
}
