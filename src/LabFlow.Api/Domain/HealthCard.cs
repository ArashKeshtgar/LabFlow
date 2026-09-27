namespace LabFlow.Api.Domain;

/// <summary>Ontario health card number (HCN) checks.</summary>
public static class HealthCard
{
    /// <summary>
    /// Strips spaces and dashes so "1234-567-897 AB" style input is accepted.
    /// </summary>
    public static string Normalize(string input) =>
        new(input.Where(char.IsAsciiLetterOrDigit).ToArray());

    /// <summary>
    /// True when <paramref name="number"/> is 10 digits and the last digit is a valid
    /// Luhn (mod 10) check digit, which is how Ontario HCNs are built.
    /// </summary>
    public static bool IsValidNumber(string? number)
    {
        if (number is null || number.Length != 10 || !number.All(char.IsAsciiDigit))
            return false;

        var sum = 0;
        for (var i = 0; i < 10; i++)
        {
            var digit = number[9 - i] - '0';
            if (i % 2 == 1)
            {
                digit *= 2;
                if (digit > 9) digit -= 9;
            }
            sum += digit;
        }
        return sum % 10 == 0;
    }

    /// <summary>Version code: empty (old red-and-white cards) or one or two letters.</summary>
    public static bool IsValidVersion(string? version) =>
        string.IsNullOrEmpty(version) || (version.Length <= 2 && version.All(char.IsAsciiLetterUpper));
}
