using System.ComponentModel.DataAnnotations;

namespace Cambrian.Application.DTOs.Subscriptions;

public class UpdateSubscriptionRequest
{
    [Required]
    [RegularExpression("^(free|paid|creator)$", ErrorMessage = "Plan must be free, paid, or creator.")]
    public string Plan { get; set; } = "";
}
