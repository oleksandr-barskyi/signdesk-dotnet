namespace SignDesk.Domain;

public enum DocumentStatus
{
    Draft = 0,
    Sent = 1,
    Completed = 2,
    Declined = 3,
    Expired = 4,
}

public enum SignerStatus
{
    Pending = 0,
    Signed = 1,
    Declined = 2,
}

public enum NotificationKind
{
    SignatureRequested = 0,
    DocumentCompleted = 1,
    DocumentDeclined = 2,
    DocumentExpired = 3,
}
