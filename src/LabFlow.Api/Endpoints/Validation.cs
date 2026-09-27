using System.Net.Mail;
using LabFlow.Api.Domain;

namespace LabFlow.Api.Endpoints;

/// <summary>Collects field errors and turns them into a 400 ValidationProblem.</summary>
public class Validator
{
    private readonly Dictionary<string, List<string>> _errors = [];

    public bool IsValid => _errors.Count == 0;

    public Validator Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list))
            _errors[field] = list = [];
        list.Add(message);
        return this;
    }

    public Validator Require(bool condition, string field, string message) =>
        condition ? this : Add(field, message);

    public IResult Problem() =>
        Results.ValidationProblem(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
}

public record PatientDetails(
    string FirstName,
    string LastName,
    DateOnly DateOfBirth,
    string Sex,
    string? HealthCardNumber,
    string? HealthCardVersion,
    string? Email,
    string? MobilePhone,
    string PreferredLanguage,
    bool ConsentEmailNotification)
{
    public string? NormalizedHealthCard =>
        string.IsNullOrWhiteSpace(HealthCardNumber) ? null : HealthCard.Normalize(HealthCardNumber);

    public string? NormalizedVersion =>
        string.IsNullOrWhiteSpace(HealthCardVersion) ? null : HealthCardVersion.Trim().ToUpperInvariant();

    private static string Key(string prefix, string field) => prefix.Length == 0 ? field : $"{prefix}.{field}";

    public void Validate(Validator v, DateOnly today, string prefix = "patient")
    {
        v.Require(!string.IsNullOrWhiteSpace(FirstName) && FirstName.Length <= 60, Key(prefix, "firstName"), "First name is required (max 60 characters).");
        v.Require(!string.IsNullOrWhiteSpace(LastName) && LastName.Length <= 60, Key(prefix, "lastName"), "Last name is required (max 60 characters).");
        v.Require(DateOfBirth >= new DateOnly(1900, 1, 1) && DateOfBirth <= today, Key(prefix, "dateOfBirth"), "Date of birth is not valid.");
        v.Require(Sex is "M" or "F" or "X" or "U", Key(prefix, "sex"), "Sex must be M, F, X or U.");
        v.Require(PreferredLanguage is "en" or "fr", Key(prefix, "preferredLanguage"), "Language must be en or fr.");
        if (NormalizedHealthCard is { } hcn)
            v.Require(HealthCard.IsValidNumber(hcn), Key(prefix, "healthCardNumber"), "Health card number must be 10 digits with a valid check digit.");
        v.Require(HealthCard.IsValidVersion(NormalizedVersion), Key(prefix, "healthCardVersion"), "Version code is one or two letters.");
        if (!string.IsNullOrWhiteSpace(Email))
            v.Require(MailAddress.TryCreate(Email, out _) && Email.Length <= 254, Key(prefix, "email"), "Email address is not valid.");
        v.Require(!ConsentEmailNotification || !string.IsNullOrWhiteSpace(Email), Key(prefix, "email"), "An email address is needed for email notifications.");
    }
}
