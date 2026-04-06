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
    public async Task GoogleLoginAsync_CalledTwiceWithSameEmail_ReturnsSameUserId()
    {
        // --- Arrange -------------------------------------------------------
        var fakePayload = new GoogleJsonWebSignature.Payload
        {
            Email = "john@gmail.com",
            EmailVerified = true,
            Subject = "google-subject-john-123",
            Name = "John Smith"
        };

        // Track the user that gets created so we can return it on subsequent calls.
        ApplicationUser? capturedUser = null;

        // Users.FirstOrDefaultAsync fallback when FindByEmailAsync returns null
        _users.Users.Returns(new AsyncEmptyListQuery<ApplicationUser>());

        // FindByEmailAsync returns whatever state capturedUser is in at call time.
        // First call: capturedUser is null  → returns null (user not found yet)
        // Second call: capturedUser is set  → returns the created user
        _users.FindByEmailAsync("john@gmail.com")
            .Returns(_ => Task.FromResult<ApplicationUser?>(capturedUser));

        // CreateAsync captures the user passed in and assigns an Id if missing.
        _users.CreateAsync(Arg.Any<ApplicationUser>())
            .Returns(callInfo =>
            {
                capturedUser = callInfo.Arg<ApplicationUser>();
                if (string.IsNullOrEmpty(capturedUser.Id))
                    capturedUser.Id = Guid.NewGuid().ToString();
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
        // Same account returned both times
        Assert.Equal(first.UserId, second.UserId);
        Assert.Equal(first.Email, second.Email);
        Assert.Equal("john@gmail.com", first.Email);

        // CreateAsync called exactly once — no duplicate row creation
        await _users.Received(1).CreateAsync(Arg.Any<ApplicationUser>());
    }

    [Fact]
    public async Task GoogleLoginAsync_ExistingEmailAccount_LeavesUsernameIntact()
    {
        // Simulate a user who registered via email and set a username.
        var existingUser = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "test@gmail.com",
            UserName = "testuser", // real username, NOT email-sentinel
            DisplayName = "Test User",
            Role = "Creator",
            Tier = "free",
            GoogleId = null // not yet linked
        };

        _users.FindByEmailAsync("test@gmail.com")
            .Returns(Task.FromResult<ApplicationUser?>(existingUser));

        _users.UpdateAsync(Arg.Any<ApplicationUser>())
            .Returns(Task.FromResult(IdentityResult.Success));

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

        var result = await sut.GoogleLoginAsync(new GoogleLoginRequest { IdToken = "fake" });

        // UserId must be the EXISTING user's id — no new account created
        Assert.Equal(Guid.Parse(existingUser.Id), result.UserId);
        // Username must be preserved — not nulled out or replaced
        Assert.Equal("testuser", result.Username);
        // No duplicate account was created
        await _users.DidNotReceive().CreateAsync(Arg.Any<ApplicationUser>());
        // GoogleId was linked to the existing account
        await _users.Received(1).UpdateAsync(Arg.Is<ApplicationUser>(u => u.GoogleId == fakePayload.Subject));
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
                   Substitute.For<ILogger<AuthService>>())
        {
            _fakePayload = fakePayload;
        }

        protected override Task<GoogleJsonWebSignature.Payload> ValidateGoogleTokenAsync(string idToken)
            => Task.FromResult(_fakePayload);
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
