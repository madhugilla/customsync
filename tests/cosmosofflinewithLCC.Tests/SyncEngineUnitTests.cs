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
            var pendingItem = new Item { Id = "1", Content = "A", LastModified = now, UserId = _userId, Type = "Item" };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item> { pendingItem });
            remoteMock.Setup(x => x.GetAsync("1", _userId)).ReturnsAsync((Item?)null);
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item> { pendingItem });

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.Id, x => x.LastModified, _userId);

            // Act
            await syncEngine.SyncAsync();

            // Assert
            remoteMock.Verify(x => x.UpsertBulkAsync(It.Is<IEnumerable<Item>>(items =>
                items.Any(i => i.Id == pendingItem.Id &&
                        i.Content == pendingItem.Content &&
                        i.LastModified == now &&
                        i.UserId == _userId &&
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
            var pendingItem = new Item { Id = "2", Content = "B", LastModified = now, UserId = _userId, Type = "Item" };
            var remoteItem = new Item { Id = "2", Content = "Old", LastModified = now.AddMinutes(-1), UserId = _userId, Type = "Item" };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item> { pendingItem });
            remoteMock.Setup(x => x.GetAsync("2", _userId)).ReturnsAsync(remoteItem);
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item>()); // Prevent the pull logic from affecting the test

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.Id, x => x.LastModified, _userId);

            // Act
            await syncEngine.SyncAsync();

            // Assert
            remoteMock.Verify(x => x.UpsertBulkAsync(It.Is<IEnumerable<Item>>(items =>
                items.Any(i => i.Id == pendingItem.Id &&
                        i.Content == pendingItem.Content &&
                        i.LastModified == now &&
                        i.UserId == _userId &&
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
            var pendingItem = new Item { Id = "3", Content = "C", LastModified = now.AddMinutes(-2), UserId = _userId, Type = "Item" };
            var remoteItem = new Item { Id = "3", Content = "Newer", LastModified = now, UserId = _userId, Type = "Item" };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item> { pendingItem });
            remoteMock.Setup(x => x.GetAsync("3", _userId)).ReturnsAsync(remoteItem);
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item> { remoteItem });

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.Id, x => x.LastModified, _userId);

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
            var remoteItem = new Item { Id = "4", Content = "Remote", LastModified = now, UserId = _userId, Type = "Item" };
            var localItem = new Item { Id = "4", Content = "Old", LastModified = now.AddMinutes(-1), UserId = _userId, Type = "Item" };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item>());
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item> { remoteItem });
            localMock.Setup(x => x.GetAsync("4", _userId)).ReturnsAsync(localItem);

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.Id, x => x.LastModified, _userId);

            // Act
            await syncEngine.SyncAsync();

            // Assert
            localMock.Verify(x => x.UpsertBulkAsync(It.Is<IEnumerable<Item>>(items =>
                items.Any(i => i.Id == remoteItem.Id &&
                        i.Content == remoteItem.Content &&
                        i.LastModified == now &&
                        i.UserId == _userId &&
                        i.Type == "Item"))), Times.Once);
        }

        [Fact]
        public async Task SyncAsync_PullsRemoteItemToLocal_WhenLocalDoesNotHaveItem()
        {
            // Arrange
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var remoteItem = new Item { Id = "5", Content = "RemoteOnly", LastModified = now, UserId = _userId, Type = "Item" };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item>());
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item> { remoteItem });
            localMock.Setup(x => x.GetAsync("5", _userId)).ReturnsAsync((Item?)null);

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.Id, x => x.LastModified, _userId);

            // Act
            await syncEngine.SyncAsync();

            // Assert
            localMock.Verify(x => x.UpsertBulkAsync(It.Is<IEnumerable<Item>>(items =>
                items.Any(i => i.Id == remoteItem.Id &&
                        i.Content == remoteItem.Content &&
                        i.LastModified == now &&
                        i.UserId == _userId &&
                        i.Type == "Item"))), Times.Once);
        }

        [Fact]
        public async Task SyncAsync_DoesNotPullRemoteItem_WhenLocalIsNewer()
        {
            // Arrange
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var remoteItem = new Item { Id = "6", Content = "OldRemote", LastModified = now.AddMinutes(-2), UserId = _userId, Type = "Item" };
            var localItem = new Item { Id = "6", Content = "NewerLocal", LastModified = now, UserId = _userId, Type = "Item" };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item>());
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item> { remoteItem });
            localMock.Setup(x => x.GetAsync("6", _userId)).ReturnsAsync(localItem);

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.Id, x => x.LastModified, _userId);

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
            var user1Item = new Item { Id = "7", Content = "User1Data", LastModified = now, UserId = "user1", Type = "Item" };
            var user2Item = new Item { Id = "8", Content = "User2Data", LastModified = now, UserId = "user2", Type = "Item" };

            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item>());
            remoteMock.Setup(x => x.GetByUserIdAsync("user1")).ReturnsAsync(new List<Item> { user1Item });
            localMock.Setup(x => x.GetAsync("7", "user1")).ReturnsAsync((Item?)null);
            localMock.Setup(x => x.GetAsync("8", "user2")).ReturnsAsync((Item?)null);

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.Id, x => x.LastModified, "user1");

            // Act
            await syncEngine.SyncAsync();

            // Assert
            localMock.Verify(x => x.UpsertBulkAsync(It.Is<IEnumerable<Item>>(items =>
                items.Any(i => i.Id == "7" && i.UserId == "user1" && i.Type == "Item"))), Times.Once);
            remoteMock.Verify(x => x.GetByUserIdAsync("user1"), Times.Once);
        }

        [Fact]
        public async Task InitialUserDataPull_ShouldPassTypeToEnsureProperties()
        {
            // Arrange
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var userItem = new Item { Id = "9", Content = "TypeTest", LastModified = now, UserId = _userId, Type = "CustomType" };
            var userItemNoType = new Item { Id = "10", Content = "NoTypeTest", LastModified = now, UserId = _userId, Type = null! };

            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item> { userItem, userItemNoType });
            localMock.Setup(x => x.GetAsync("9", _userId)).ReturnsAsync((Item?)null);
            localMock.Setup(x => x.GetAsync("10", _userId)).ReturnsAsync((Item?)null);

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.Id, x => x.LastModified, _userId);

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
            var pendingItem = new Item { Id = "11", Content = "NoType", LastModified = now, UserId = _userId, Type = null! };

            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item> { pendingItem });
            remoteMock.Setup(x => x.GetAsync("11", _userId)).ReturnsAsync((Item?)null);
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item>());

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.Id, x => x.LastModified, _userId);

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
                Id = "type-test",
                Content = "Type-specific item",
                LastModified = now,
                UserId = _userId,
                Type = "CustomType"
            };

            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item> { localItem });
            remoteMock.Setup(x => x.GetAsync("type-test", _userId)).ReturnsAsync((Item?)null);
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item>());

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.Id, x => x.LastModified, _userId);

            // Act
            await syncEngine.SyncAsync();

            // Assert
            remoteMock.Verify(x => x.UpsertBulkAsync(It.Is<IEnumerable<Item>>(items =>
                items.Any(i => i.Id == "type-test" &&
                       i.UserId == _userId &&
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
                Id = "typeless",
                Content = "No type specified",
                LastModified = now,
                UserId = _userId,
                Type = null! // Type will be null
            };

            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item> { remoteItem });
            localMock.Setup(x => x.GetAsync("typeless", _userId)).ReturnsAsync((Item?)null);

            var syncEngine = new SyncEngine<Item>(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.Id, x => x.LastModified, _userId);

            // Act
            await syncEngine.InitialUserDataPullAsync("SpecifiedType");

            // Assert
            localMock.Verify(x => x.UpsertBulkAsync(It.Is<IEnumerable<Item>>(items =>
                items.Any(i => i.Id == "typeless" &&
                       i.UserId == _userId))), Times.Once);
        }
    }
}
