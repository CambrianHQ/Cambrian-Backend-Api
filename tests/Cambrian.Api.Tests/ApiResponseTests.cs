using Cambrian.Api.Common;

namespace Cambrian.Api.Tests;

public sealed class ApiResponseTests
{
    [Fact]
    public void Ok_WithData_SetsSuccessTrue()
    {
        var result = ApiResponse<string>.Ok("hello");

        Assert.True(result.Success);
        Assert.Equal("hello", result.Data);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Ok_WithDataAndMessage_IncludesMessage()
    {
        var result = ApiResponse<int>.Ok(42, "found");

        Assert.True(result.Success);
        Assert.Equal(42, result.Data);
        Assert.Equal("found", result.Message);
    }

    [Fact]
    public void Fail_SetsSuccessFalse()
    {
        var result = ApiResponse<string>.Fail("bad request");

        Assert.False(result.Success);
        Assert.Equal("bad request", result.Error);
        Assert.Null(result.Data);
    }

    [Fact]
    public void NonGeneric_Ok_ReturnsMessageOnly()
    {
        var result = ApiResponse.Ok("done");

        Assert.True(result.Success);
        Assert.Equal("done", result.Message);
        Assert.Null(result.Data);
    }

    [Fact]
    public void NonGeneric_Fail_ReturnsError()
    {
        var result = ApiResponse.Fail("oops");

        Assert.False(result.Success);
        Assert.Equal("oops", result.Error);
    }
}
