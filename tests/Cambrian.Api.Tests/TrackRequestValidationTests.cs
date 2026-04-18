using System.ComponentModel.DataAnnotations;
using Cambrian.Application.DTOs.Catalog;

namespace Cambrian.Api.Tests;

public sealed class TrackRequestValidationTests
{
    [Theory]
    [InlineData(nameof(EditTrackRequest.NonExclusivePriceCents))]
    [InlineData(nameof(EditTrackRequest.ExclusivePriceCents))]
    [InlineData(nameof(EditTrackRequest.CopyrightBuyoutPriceCents))]
    public void EditTrackRequest_Rejects_NonPositivePriceCents(string propertyName)
    {
        var request = new EditTrackRequest();
        SetProperty(request, propertyName, 0);

        var results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(propertyName));

        request = new EditTrackRequest();
        SetProperty(request, propertyName, -1);

        results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(propertyName));
    }

    [Theory]
    [InlineData(nameof(EditTrackRequest.NonExclusivePriceCents))]
    [InlineData(nameof(EditTrackRequest.ExclusivePriceCents))]
    [InlineData(nameof(EditTrackRequest.CopyrightBuyoutPriceCents))]
    public void EditTrackRequest_Allows_PositivePriceCents_And_Null(string propertyName)
    {
        var validRequest = new EditTrackRequest();
        SetProperty(validRequest, propertyName, 1);

        Assert.DoesNotContain(Validate(validRequest), r => r.MemberNames.Contains(propertyName));

        var nullRequest = new EditTrackRequest();
        SetProperty(nullRequest, propertyName, null);

        Assert.DoesNotContain(Validate(nullRequest), r => r.MemberNames.Contains(propertyName));
    }

    [Theory]
    [InlineData(nameof(UploadTrackRequest.NonExclusivePrice))]
    [InlineData(nameof(UploadTrackRequest.ExclusivePrice))]
    [InlineData(nameof(UploadTrackRequest.CopyrightBuyoutPrice))]
    public void UploadTrackRequest_Rejects_NonPositivePrices(string propertyName)
    {
        var request = new UploadTrackRequest
        {
            Title = "Beat"
        };
        SetProperty(request, propertyName, 0m);

        var results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(propertyName));

        request = new UploadTrackRequest
        {
            Title = "Beat"
        };
        SetProperty(request, propertyName, -0.01m);

        results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(propertyName));
    }

    [Theory]
    [InlineData(nameof(UploadTrackRequest.NonExclusivePrice))]
    [InlineData(nameof(UploadTrackRequest.ExclusivePrice))]
    [InlineData(nameof(UploadTrackRequest.CopyrightBuyoutPrice))]
    public void UploadTrackRequest_Allows_PositivePrices_And_Null(string propertyName)
    {
        var validRequest = new UploadTrackRequest
        {
            Title = "Beat"
        };
        SetProperty(validRequest, propertyName, 0.01m);

        Assert.DoesNotContain(Validate(validRequest), r => r.MemberNames.Contains(propertyName));

        var nullRequest = new UploadTrackRequest
        {
            Title = "Beat"
        };
        SetProperty(nullRequest, propertyName, null);

        Assert.DoesNotContain(Validate(nullRequest), r => r.MemberNames.Contains(propertyName));
    }

    private static List<ValidationResult> Validate(object instance)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);
        return results;
    }

    private static void SetProperty(object instance, string propertyName, object? value)
    {
        instance.GetType().GetProperty(propertyName)!.SetValue(instance, value);
    }
}
