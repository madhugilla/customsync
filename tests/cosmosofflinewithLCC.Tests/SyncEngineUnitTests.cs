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
            var pendingItem = new Item { Id = "1", Content = "A", LastModified = now, UserId = _userId };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item> { pendingItem });
            remoteMock.Setup(x => x.GetAsync("1")).ReturnsAsync((Item?)null);
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item> { pendingItem });

            // Act
            await SyncEngine.SyncAsync(localMock.Object, remoteMock.Object, _loggerMock.Object, x => x.Id, x => x.LastModified, _userId);

            // Assert
            remoteMock.Verify(x => x.UpsertBulkAsync(It.Is<IEnumerable<Item>>(items => items.Any(i => i.Id == pendingItem.Id && i.Content == pendingItem.Content && i.LastModified == now))), Times.Once);
            localMock.Verify(x => x.RemovePendingChangeAsync("1"), Times.Once);
            localMock.Verify(x => x.GetPendingChangesAsync(), Times.Once);
            remoteMock.Verify(x => x.GetAsync("1"), Times.Once);
        }

        [Fact]
        public async Task SyncAsync_PushesLocalPendingChangeToRemote_WhenLocalIsNewerThanRemote()
        {
            // Arrange
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var pendingItem = new Item { Id = "2", Content = "B", LastModified = now, UserId = _userId };
            var remoteItem = new Item { Id = "2", Content = "Old", LastModified = now.AddMinutes(-1), UserId = _userId };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item> { pendingItem });
            remoteMock.Setup(x => x.GetAsync("2")).ReturnsAsync(remoteItem);
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item>()); // Prevent the pull logic from affecting the test

            // Act
            await SyncEngine.SyncAsync(localMock.Object, remoteMock.Object, _loggerMock.Object, x => x.Id, x => x.LastModified, _userId);

            // Assert
            remoteMock.Verify(x => x.UpsertBulkAsync(It.Is<IEnumerable<Item>>(items => items.Any(i => i.Id == pendingItem.Id && i.Content == pendingItem.Content && i.LastModified == now))), Times.Once);
            localMock.Verify(x => x.RemovePendingChangeAsync("2"), Times.Once);
        }

        [Fact]
        public async Task SyncAsync_DoesNotPushLocalPendingChange_WhenRemoteIsNewer()
        {
            // Arrange
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var pendingItem = new Item { Id = "3", Content = "C", LastModified = now.AddMinutes(-2), UserId = _userId };
            var remoteItem = new Item { Id = "3", Content = "Newer", LastModified = now, UserId = _userId };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item> { pendingItem });
            remoteMock.Setup(x => x.GetAsync("3")).ReturnsAsync(remoteItem);

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
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var remoteItem = new Item { Id = "4", Content = "Remote", LastModified = now, UserId = _userId };
            var localItem = new Item { Id = "4", Content = "Old", LastModified = now.AddMinutes(-1), UserId = _userId };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item>());
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item> { remoteItem });
            localMock.Setup(x => x.GetAsync("4")).ReturnsAsync(localItem);

            // Act
            await SyncEngine.SyncAsync(localMock.Object, remoteMock.Object, _loggerMock.Object, x => x.Id, x => x.LastModified, _userId);

            // Assert
            localMock.Verify(x => x.UpsertBulkAsync(It.Is<IEnumerable<Item>>(items => items.Any(i => i.Id == remoteItem.Id && i.Content == remoteItem.Content && i.LastModified == now))), Times.Once);
        }

        [Fact]
        public async Task SyncAsync_PullsRemoteItemToLocal_WhenLocalDoesNotHaveItem()
        {
            // Arrange
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var remoteItem = new Item { Id = "5", Content = "RemoteOnly", LastModified = now, UserId = _userId };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item>());
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item> { remoteItem });
            localMock.Setup(x => x.GetAsync("5")).ReturnsAsync((Item?)null);

            // Act
            await SyncEngine.SyncAsync(localMock.Object, remoteMock.Object, _loggerMock.Object, x => x.Id, x => x.LastModified, _userId);

            // Assert
            localMock.Verify(x => x.UpsertBulkAsync(It.Is<IEnumerable<Item>>(items => items.Any(i => i.Id == remoteItem.Id && i.Content == remoteItem.Content && i.LastModified == now))), Times.Once);
        }

        [Fact]
        public async Task SyncAsync_DoesNotPullRemoteItem_WhenLocalIsNewer()
        {
            // Arrange
            var localMock = new Mock<IDocumentStore<Item>>();
            var remoteMock = new Mock<IDocumentStore<Item>>();
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
            var remoteItem = new Item { Id = "6", Content = "OldRemote", LastModified = now.AddMinutes(-2), UserId = _userId };
            var localItem = new Item { Id = "6", Content = "NewerLocal", LastModified = now, UserId = _userId };
            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item>());
            remoteMock.Setup(x => x.GetByUserIdAsync(_userId)).ReturnsAsync(new List<Item> { remoteItem });
            localMock.Setup(x => x.GetAsync("6")).ReturnsAsync(localItem);

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
            var now = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);

            // Create items for multiple users
            var user1Item = new Item { Id = "7", Content = "User1Data", LastModified = now, UserId = "user1" };
            var user2Item = new Item { Id = "8", Content = "User2Data", LastModified = now, UserId = "user2" };

            localMock.Setup(x => x.GetPendingChangesAsync()).ReturnsAsync(new List<Item>());
            remoteMock.Setup(x => x.GetByUserIdAsync("user1")).ReturnsAsync(new List<Item> { user1Item });
            localMock.Setup(x => x.GetAsync("7")).ReturnsAsync((Item?)null);
            localMock.Setup(x => x.GetAsync("8")).ReturnsAsync((Item?)null);

            // Act
            await SyncEngine.SyncAsync(localMock.Object, remoteMock.Object, _loggerMock.Object, x => x.Id, x => x.LastModified, "user1");

            // Assert
            // Should sync user1's item but not user2's
            localMock.Verify(x => x.UpsertBulkAsync(It.Is<IEnumerable<Item>>(items =>
                items.Any(i => i.Id == "7" && i.UserId == "user1") &&
                !items.Any(i => i.Id == "8" && i.UserId == "user2"))), Times.Once);

            // Verify we specifically called GetByUserIdAsync with the right userId
            remoteMock.Verify(x => x.GetByUserIdAsync("user1"), Times.Once);
        }
    }
}
