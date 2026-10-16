using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL;

namespace Countflow.SharedKernel.Modules;

/// <summary>
/// Pins sequential module transactions to the session holding an orchestration
/// lock. This shares a connection, never a transaction or business-data access.
/// Losing that session fails the request; it must not reconnect without its lock.
/// </summary>
public sealed class ModuleSession(IServiceProvider services)
{
    private static readonly ConditionalWeakTable<DbContext, ModuleSession> Pinned = new();
    private DbConnection? _connection;

    internal static IExecutionStrategy CreateStrategy(ExecutionStrategyDependencies dependencies) => new ModuleExecutionStrategy(dependencies);

    internal static IExecutionStrategy StrategyFor(ExecutionStrategyDependencies dependencies) =>
        Pinned.TryGetValue(dependencies.CurrentContext.Context, out var session)
            ? new PinnedExecutionStrategy(dependencies, session)
            : new NpgsqlRetryingExecutionStrategy(dependencies, 5, TimeSpan.FromSeconds(10), null);

    internal static bool HasPin(DbContext context) => Pinned.TryGetValue(context, out _);
    public bool IsPinned => _connection is not null;

    public IDisposable Pin(DbConnection connection)
    {
        if (IsPinned) throw new InvalidOperationException("A module session is already pinned.");
        var contexts = services.GetServices<IModuleDbContext>().Cast<DbContext>().ToArray();
        var databases = contexts.Select(context => context.Database).ToArray();
        if (databases.Any(db => db.CurrentTransaction is not null || db.GetDbConnection().State != ConnectionState.Closed))
            throw new InvalidOperationException("Module sessions must be pinned before opening a context connection.");
        var original = contexts.Select(context => (Context: context, Database: context.Database, ConnectionString: context.Database.GetConnectionString()!)).ToArray();
        foreach (var db in databases) db.SetDbConnection(connection, contextOwnsConnection: false);
        _connection = connection;
        foreach (var context in contexts) Pinned.Add(context, this);
        return new PinScope(this, original);
    }

    internal void AssertOpen()
    {
        if (_connection?.State != ConnectionState.Open)
            throw new InvalidOperationException("The locked module session was lost; resume the request with its original key.");
    }

    private sealed class PinScope(ModuleSession session, (DbContext Context, DatabaseFacade Database, string ConnectionString)[] original) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in original)
            {
                item.Database.SetDbConnection(new NpgsqlConnection(item.ConnectionString), contextOwnsConnection: true);
                Pinned.Remove(item.Context);
            }
            session._connection = null;
        }
    }
}

internal sealed class ModuleExecutionStrategy(ExecutionStrategyDependencies dependencies) : IExecutionStrategy
{
    public bool RetriesOnFailure => !ModuleSession.HasPin(dependencies.CurrentContext.Context);
    public TResult Execute<TState, TResult>(TState state, Func<DbContext, TState, TResult> operation,
        Func<DbContext, TState, ExecutionResult<TResult>>? verifySucceeded) =>
        ModuleSession.StrategyFor(dependencies).Execute(state, operation, verifySucceeded);
    public Task<TResult> ExecuteAsync<TState, TResult>(TState state,
        Func<DbContext, TState, CancellationToken, Task<TResult>> operation,
        Func<DbContext, TState, CancellationToken, Task<ExecutionResult<TResult>>>? verifySucceeded,
        CancellationToken cancellationToken = default) =>
        ModuleSession.StrategyFor(dependencies).ExecuteAsync(state, operation, verifySucceeded, cancellationToken);
}

internal sealed class PinnedExecutionStrategy(ExecutionStrategyDependencies dependencies, ModuleSession session) : IExecutionStrategy
{
    public bool RetriesOnFailure => false;

    public TResult Execute<TState, TResult>(TState state, Func<DbContext, TState, TResult> operation,
        Func<DbContext, TState, ExecutionResult<TResult>>? verifySucceeded)
    {
        session.AssertOpen();
        return operation(dependencies.CurrentContext.Context, state);
    }

    public Task<TResult> ExecuteAsync<TState, TResult>(TState state,
        Func<DbContext, TState, CancellationToken, Task<TResult>> operation,
        Func<DbContext, TState, CancellationToken, Task<ExecutionResult<TResult>>>? verifySucceeded,
        CancellationToken cancellationToken = default)
    {
        session.AssertOpen();
        return operation(dependencies.CurrentContext.Context, state, cancellationToken);
    }
}
