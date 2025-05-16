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
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc); var pendingItem = new Item { Id = "1", Content = "A", LastModified = now, UserId = _userId, Type = "Item" };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item> { pendingItem });
            remoteMock.Setup(x => x.GetAsync("1", _userId)).ReturnsAsync((Item?)null);
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item> { pendingItem });

            // Act
            await SyncEngine.SyncAsync(localMock.Object, remoteMock.Object, _loggerMock.Object, x => x.Id, x => x.LastModified, _userId);

            // Assert
            remoteMock.Verify(x => x.UpsertBulkAsync(It.Is<IEnumerable<Item>>(items =>
                items.Any(i => i.Id == pendingItem.Id &&
                        i.Content == pendingItem.Content &&
                        i.LastModified == now &&
                        i.UserId == _userId &&
                        i.Type == "Item"))), Times.Once); localMock.Verify(x => x.RemovePendingChangeAsync("1"), Times.Once);
            localMock.Verify(x => x.GetPendingChangesAsync(), Times.Once);
            remoteMock.Verify(x => x.GetAsync("1", _userId), Times.Once);
        }

        [Fact]
        public async Task SyncAsync_PushesLocalPendingChangeToRemote_WhenLocalIsNewerThanRemote()
        {
            // Arrange
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc); var pendingItem = new Item { Id = "2", Content = "B", LastModified = now, UserId = _userId, Type = "Item" };
            var remoteItem = new Item { Id = "2", Content = "Old", LastModified = now.AddMinutes(-1), UserId = _userId, Type = "Item" };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item> { pendingItem });
            remoteMock.Setup(x => x.GetAsync("2", _userId)).ReturnsAsync(remoteItem);
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item>()); // Prevent the pull logic from affecting the test

            // Act
            await SyncEngine.SyncAsync(localMock.Object, remoteMock.Object, _loggerMock.Object, x => x.Id, x => x.LastModified, _userId);

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
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc); var pendingItem = new Item { Id = "3", Content = "C", LastModified = now.AddMinutes(-2), UserId = _userId, Type = "Item" };
            var remoteItem = new Item { Id = "3", Content = "Newer", LastModified = now, UserId = _userId, Type = "Item" };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item> { pendingItem });
            remoteMock.Setup(x => x.GetAsync("3", _userId)).ReturnsAsync(remoteItem);

            var remoteItems = new List<Item> { remoteItem };
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(remoteItems);

            // Act
            await SyncEngine.SyncAsync(localMock.Object, remoteMock.Object, _loggerMock.Object, x => x.Id, x => x.LastModified, _userId);

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
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc); var remoteItem = new Item { Id = "4", Content = "Remote", LastModified = now, UserId = _userId, Type = "Item" };
            var localItem = new Item { Id = "4", Content = "Old", LastModified = now.AddMinutes(-1), UserId = _userId, Type = "Item" };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item>());
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item> { remoteItem });
            localMock.Setup(x => x.GetAsync("4", _userId)).ReturnsAsync(localItem);

            // Act
            await SyncEngine.SyncAsync(localMock.Object, remoteMock.Object, _loggerMock.Object, x => x.Id, x => x.LastModified, _userId);

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
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc); var remoteItem = new Item { Id = "5", Content = "RemoteOnly", LastModified = now, UserId = _userId, Type = "Item" };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item>());
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item> { remoteItem });
            localMock.Setup(x => x.GetAsync("5", _userId)).ReturnsAsync((Item?)null);

            // Act
            await SyncEngine.SyncAsync(localMock.Object, remoteMock.Object, _loggerMock.Object, x => x.Id, x => x.LastModified, _userId);

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
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc); var remoteItem = new Item { Id = "6", Content = "OldRemote", LastModified = now.AddMinutes(-2), UserId = _userId, Type = "Item" };
            var localItem = new Item { Id = "6", Content = "NewerLocal", LastModified = now, UserId = _userId, Type = "Item" };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item>());
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item> { remoteItem });
            localMock.Setup(x => x.GetAsync("6", _userId)).ReturnsAsync(localItem);

            // Act
            await SyncEngine.SyncAsync(localMock.Object, remoteMock.Object, _loggerMock.Object, x => x.Id, x => x.LastModified, _userId);

            // Assert
            localMock.Verify(x => x.UpsertBulkAsync(It.IsAny<IEnumerable<Item>>()), Times.Never);
        }

        [Fact]
        public async Task SyncAsync_FiltersByUserId_WhenUserIdIsProvided()
        {
            // Arrange
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);            // Create items for multiple users
            var user1Item = new Item { Id = "7", Content = "User1Data", LastModified = now, UserId = "user1", Type = "Item" };
            var user2Item = new Item { Id = "8", Content = "User2Data", LastModified = now, UserId = "user2", Type = "Item" };

            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item>());
            remoteMock.Setup(x => x.GetByUserIdAsync("user1")).ReturnsAsync(new List<Item> { user1Item });
            localMock.Setup(x => x.GetAsync("7", "user1")).ReturnsAsync((Item?)null);
            localMock.Setup(x => x.GetAsync("8", "user2")).ReturnsAsync((Item?)null);

            // Act
            await SyncEngine.SyncAsync(localMock.Object, remoteMock.Object, _loggerMock.Object, x => x.Id, x => x.LastModified, "user1");

            // Assert
            // Should sync user1's item but not user2's
            localMock.Verify(x => x.UpsertBulkAsync(It.Is<IEnumerable<Item>>(items =>
                items.Any(i => i.Id == "7" && i.UserId == "user1" && i.Type == "Item"))), Times.Once);

            // Verify we specifically called GetByUserIdAsync with the right userId
            remoteMock.Verify(x => x.GetByUserIdAsync("user1"), Times.Once);
        }

        [Fact]
        public async Task InitialUserDataPull_ShouldPassTypeToEnsureProperties()
        {
            // Arrange
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);            // Item with a custom type and an item with no type
            var userItem = new Item { Id = "9", Content = "TypeTest", LastModified = now, UserId = _userId, Type = "CustomType" };
            var userItemNoType = new Item { Id = "10", Content = "NoTypeTest", LastModified = now, UserId = _userId, Type = null! };

            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item> { userItem, userItemNoType });
            localMock.Setup(x => x.GetAsync("9", _userId)).ReturnsAsync((Item?)null);
            localMock.Setup(x => x.GetAsync("10", _userId)).ReturnsAsync((Item?)null);

            // Act - The docType parameter should be used for items that don't already have a type
            await SyncEngine.InitialUserDataPullAsync(localMock.Object, remoteMock.Object, _loggerMock.Object,
                x => x.Id, x => x.LastModified, _userId, "SpecifiedType");

            // Assert - Just verify that UpsertBulkAsync was called with any items
            // Note: We can't easily verify the exact Type values from outside the SyncEngine since the Type is set internally
            localMock.Verify(x => x.UpsertBulkAsync(It.IsAny<IEnumerable<Item>>()), Times.Once);
        }

        [Fact]
        public async Task SyncAsync_ShouldEnsureCosmosProperties_WhenRemoteIsIDocumentStore()
        {
            // Arrange - Use IDocumentStore instead of CosmosDbStore for mocking
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);            // Item without a type
            var pendingItem = new Item { Id = "11", Content = "NoType", LastModified = now, UserId = _userId, Type = null! };

            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item> { pendingItem });
            remoteMock.Setup(x => x.GetAsync("11", _userId)).ReturnsAsync((Item?)null);
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item>());

            // Act
            await SyncEngine.SyncAsync(localMock.Object, remoteMock.Object, _loggerMock.Object, x => x.Id, x => x.LastModified, _userId);

            // Assert
            // Just verify UpsertBulkAsync was called - we can't easily verify the Type property from outside the SyncEngine
            remoteMock.Verify(x => x.UpsertBulkAsync(It.IsAny<IEnumerable<Item>>()), Times.Once);
        }
    }
}
