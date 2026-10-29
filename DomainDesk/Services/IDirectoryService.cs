using System.Security;

namespace DomainDesk.Services;

public interface IDirectoryService : IDisposable
{
    DirectorySession Session { get; }
    Task<QueryResult> QueryAsync(DirectoryKind kind, string search, DirectoryQueryScope? scope = null);
    Task ExecuteAsync(DirectoryItem target, AccountAction action, SecureString? password = null, bool mustChangePassword = true);
    Task<ContainerResult> GetContainersAsync();
    Task CreateUserAsync(NewUserSpec spec, SecureString password);
    Task CreateComputerAsync(NewComputerSpec spec);
    Task CreateGroupAsync(NewGroupSpec spec);
    Task<QueryResult> GetGroupUsersAsync(DirectoryItem group);
    Task ChangeGroupUserAsync(DirectoryItem group, DirectoryItem user, bool add);
    Task UpdateComputerAsync(DirectoryItem computer, ComputerChanges changes);
    Task DeleteComputerAsync(DirectoryItem computer);
}
