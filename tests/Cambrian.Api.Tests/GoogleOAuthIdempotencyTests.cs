using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Cambrian.Application.Configuration;
using Cambrian.Application.DTOs.Auth;
using Cambrian.Application.Interfaces;
using Cambrian.Application.Services;
using Cambrian.Domain.Entities;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cambrian.Api.Tests;

/// <summary>
/// Verifies that POST /auth/google (GoogleLoginAsync) is idempotent:
/// calling it twice with the same email must return the same userId
/// and must never create duplicate accounts in the database.
/// </summary>
[Trait("Category", "Critical")]
public sealed class GoogleOAuthIdempotencyTests
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly IOptions<JwtSettings> _jwtOptions;
    private readonly IOptions<GoogleSettings> _googleOptions;
    private readonly ISubscriptionRepository _subscriptions;

    public GoogleOAuthIdempotencyTests()
    {
        var store = Substitute.For<IUserStore<ApplicationUser>>();
        _users = Substitute.For<UserManager<ApplicationUser>>(
            store, null, null, null, null, null, null, null, null);

        _jwtOptions = Options.Create(new JwtSettings
        {
            Key = "test-secret-key-that-is-long-enough-for-hmac256!",
            Issuer = "cambrian-api",
            Audience = "cambrian-client"
        });

        _googleOptions = Options.Create(new GoogleSettings { ClientId = "fake-google-client-id" });
        _subscriptions = Substitute.For<ISubscriptionRepository>();
    }

    [Fact]
    public async Task GoogleLoginAsync_CalledTwiceWithSameSubject_ReturnsSameUserId()
    {
        // --- Arrange -------------------------------------------------------
        // Post-audit: Google login dedups by GoogleId (Subject), NOT by email.
        // The mocked Users queryable returns capturedUser whenever its GoogleId
        // matches the payload's Subject.
        var fakePayload = new GoogleJsonWebSignature.Payload
        {
            Email = "john@gmail.com",
            EmailVerified = true,
            Subject = "google-subject-john-123",
            Name = "John Smith"
        };

        var backingList = new List<ApplicationUser>();
        ApplicationUser? capturedUser = null;

        // _users.Users — returns the live backingList wrapped as an async-capable IQueryable.
        _users.Users.Returns(_ => new AsyncListQuery<ApplicationUser>(backingList));

        // No existing local account by email — only Google identity counts.
        _users.FindByEmailAsync("john@gmail.com")
            .Returns(Task.FromResult<ApplicationUser?>(null));

        // CreateAsync captures the user, assigns an Id, and adds it to the backing list
        // so the GoogleId lookup on the second call finds it.
        _users.CreateAsync(Arg.Any<ApplicationUser>())
            .Returns(callInfo =>
            {
                capturedUser = callInfo.Arg<ApplicationUser>();
                if (string.IsNullOrEmpty(capturedUser.Id))
                    capturedUser.Id = Guid.NewGuid().ToString();
                backingList.Add(capturedUser);
                return Task.FromResult(IdentityResult.Success);
            });

        var sut = new TestableAuthService(
            _users, _jwtOptions, _googleOptions,
            _subscriptions, fakePayload);

        var request = new GoogleLoginRequest { IdToken = "fake-token" };

        // --- Act -----------------------------------------------------------
        var first = await sut.GoogleLoginAsync(request);
        var second = await sut.GoogleLoginAsync(request);

        // --- Assert --------------------------------------------------------
        Assert.Equal(first.UserId, second.UserId);
        Assert.Equal(first.Email, second.Email);
        Assert.Equal("john@gmail.com", first.Email);

        // CreateAsync called exactly once — no duplicate row creation
        await _users.Received(1).CreateAsync(Arg.Any<ApplicationUser>());
    }

    [Fact]
    public async Task GoogleLoginAsync_ExistingLocalAccount_RefusesSilentLink()
    {
        // Post-audit security fix: an existing local account (no GoogleId) must NOT
        // be silently linked to an arbitrary Google identity that happens to share
        // its email. The user must explicitly link from settings.
        var existingUser = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "test@gmail.com",
            UserName = "testuser",
            DisplayName = "Test User",
            Role = "Creator",
            Tier = "free",
            GoogleId = null // not yet linked
        };

        // No GoogleId match (the new lookup goes first).
        _users.Users.Returns(new AsyncListQuery<ApplicationUser>(new List<ApplicationUser>()));

        // But there IS a local account with this email.
        _users.FindByEmailAsync("test@gmail.com")
            .Returns(Task.FromResult<ApplicationUser?>(existingUser));

        var fakePayload = new GoogleJsonWebSignature.Payload
        {
            Email = "test@gmail.com",
            EmailVerified = true,
            Subject = "google-subject-test-456",
            Name = "Test User From Google"
        };

        var sut = new TestableAuthService(
            _users, _jwtOptions, _googleOptions,
            _subscriptions, fakePayload);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.GoogleLoginAsync(new GoogleLoginRequest { IdToken = "fake" }));
        Assert.Contains("already exists", ex.Message);

        // Critically: no UpdateAsync (silent linking) and no CreateAsync (duplicate).
        await _users.DidNotReceive().UpdateAsync(Arg.Any<ApplicationUser>());
        await _users.DidNotReceive().CreateAsync(Arg.Any<ApplicationUser>());
    }

    // ── Test infrastructure ──────────────────────────────────────────────────

    /// <summary>
    /// Subclass of AuthService that bypasses real Google token validation.
    /// Allows testing the deduplication logic without a real Google ID token.
    /// </summary>
    private sealed class TestableAuthService : AuthService
    {
        private readonly GoogleJsonWebSignature.Payload _fakePayload;

        public TestableAuthService(
            UserManager<ApplicationUser> users,
            IOptions<JwtSettings> jwtOptions,
            IOptions<GoogleSettings> googleOptions,
            ISubscriptionRepository subscriptions,
            GoogleJsonWebSignature.Payload fakePayload)
            : base(users, jwtOptions, googleOptions, subscriptions,
                   Substitute.For<IEmailService>(),
                   Substitute.For<ISmsService>(),
                   new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                       .AddInMemoryCollection(new Dictionary<string, string?> { ["App:FrontendUrl"] = "https://test" })
                       .Build(),
                   Substitute.For<ILogger<AuthService>>())
        {
            _fakePayload = fakePayload;
        }

        protected override Task<GoogleJsonWebSignature.Payload> ValidateGoogleTokenAsync(string idToken)
            => Task.FromResult(_fakePayload);
    }

    /// <summary>
    /// Minimal async-capable IQueryable wrapping a live List, so its contents can
    /// mutate between calls (used to simulate user creation between Google login attempts).
    /// </summary>
    private sealed class AsyncListQuery<T> : IQueryable<T>, IAsyncEnumerable<T>
    {
        private readonly List<T> _items;
        private readonly IQueryable<T> _q;

        public AsyncListQuery(List<T> items)
        {
            _items = items;
            _q = items.AsQueryable();
        }

        public Type ElementType => _q.ElementType;
        public Expression Expression => _q.Expression;
        IQueryProvider IQueryable.Provider => new AsyncQueryProvider<T>(_q.Provider);

        IEnumerator<T> IEnumerable<T>.GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new AsyncEnumerator<T>(_items.GetEnumerator());
    }

    /// <summary>
    /// Minimal async-capable empty IQueryable for use with EF Core's
    /// FirstOrDefaultAsync and similar extension methods.
    /// </summary>
    private sealed class AsyncEmptyListQuery<T> : IQueryable<T>, IAsyncEnumerable<T>
    {
        private static readonly List<T> _empty = [];
        private readonly IQueryable<T> _q = _empty.AsQueryable();

        public Type ElementType => _q.ElementType;
        public Expression Expression => _q.Expression;
        IQueryProvider IQueryable.Provider => new AsyncQueryProvider<T>(_q.Provider);

        IEnumerator<T> IEnumerable<T>.GetEnumerator() => _empty.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _empty.GetEnumerator();

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new AsyncEnumerator<T>(_empty.GetEnumerator());
    }

    private sealed class AsyncQueryProvider<T> : IQueryProvider, IAsyncQueryProvider
    {
        private readonly IQueryProvider _inner;
        public AsyncQueryProvider(IQueryProvider inner) => _inner = inner;

        public IQueryable CreateQuery(Expression e) => _inner.CreateQuery(e);
        public IQueryable<TElement> CreateQuery<TElement>(Expression e) => _inner.CreateQuery<TElement>(e);
        public object? Execute(Expression e) => _inner.Execute(e);
        public TResult Execute<TResult>(Expression e) => _inner.Execute<TResult>(e);

        public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
        {
            var result = _inner.Execute(expression);
            var taskResultType = typeof(TResult).GetGenericArguments()[0];
            return (TResult)typeof(Task)
                .GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(taskResultType)
                .Invoke(null, new[] { result })!;
        }
    }

    private sealed class AsyncEnumerator<T> : IAsyncEnumerator<T>
    {
        private readonly IEnumerator<T> _inner;
        public AsyncEnumerator(IEnumerator<T> inner) => _inner = inner;
        public T Current => _inner.Current;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(_inner.MoveNext());
        public ValueTask DisposeAsync() { _inner.Dispose(); return ValueTask.CompletedTask; }
    }
}
