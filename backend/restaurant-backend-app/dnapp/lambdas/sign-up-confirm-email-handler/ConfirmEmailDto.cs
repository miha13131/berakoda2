namespace SendConfirmation;

public class ConfirmEmailDto
{
    public required string Email { get; set; }
    public required string VerificationCode { get; set; }
}