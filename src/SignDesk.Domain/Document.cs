namespace SignDesk.Domain;

public sealed class Document
{
    public const int MaxSigners = 10;
    public static readonly TimeSpan MinLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(90);

    private readonly List<Signer> _signers = [];
    private readonly List<DomainEvent> _events = [];

    private Document()
    {
        Title = string.Empty;
        OwnerEmail = string.Empty;
    }

    public Document(string title, string ownerEmail, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new DomainException("title_required", "Title is required.");
        }

        Id = Guid.CreateVersion7();
        Title = title.Trim();
        OwnerEmail = NormalizeEmail(ownerEmail);
        Status = DocumentStatus.Draft;
        CreatedAt = now;
        Raise(new DomainEvent("document.created", OwnerEmail, Title));
    }

    public Guid Id { get; private set; }
    public string Title { get; private set; }
    public string OwnerEmail { get; private set; }
    public DocumentStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public uint Version { get; private set; }

    public IReadOnlyList<Signer> Signers => _signers;
    public IReadOnlyList<DomainEvent> Events => _events;

    public void ClearEvents() => _events.Clear();

    public Signer AddSigner(string email, string name)
    {
        EnsureDraft();
        var normalized = NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("signer_name_required", "Signer name is required.");
        }

        if (_signers.Count >= MaxSigners)
        {
            throw new DomainException("too_many_signers", $"A document can have at most {MaxSigners} signers.");
        }

        if (_signers.Exists(s => s.Email == normalized))
        {
            throw new DomainException("duplicate_signer", $"{normalized} is already a signer.");
        }

        var signer = new Signer(Id, normalized, name.Trim(), _signers.Count + 1);
        _signers.Add(signer);
        Raise(new DomainEvent("signer.added", OwnerEmail, normalized));
        return signer;
    }

    public void RemoveSigner(Guid signerId)
    {
        EnsureDraft();
        var signer = FindSigner(signerId);
        _signers.Remove(signer);
        var order = 1;
        foreach (var s in _signers.OrderBy(s => s.Order))
        {
            s.Order = order++;
        }

        Raise(new DomainEvent("signer.removed", OwnerEmail, signer.Email));
    }

    public void Send(DateTimeOffset now, TimeSpan lifetime)
    {
        EnsureDraft();
        if (_signers.Count == 0)
        {
            throw new DomainException("no_signers", "Add at least one signer before sending.");
        }

        if (lifetime < MinLifetime || lifetime > MaxLifetime)
        {
            throw new DomainException("invalid_lifetime", "Lifetime must be between 1 hour and 90 days.");
        }

        Status = DocumentStatus.Sent;
        SentAt = now;
        ExpiresAt = now + lifetime;
        Raise(new DomainEvent("document.sent", OwnerEmail, $"expires {ExpiresAt:O}"));
        RequestNextSignature();
    }

    public void Sign(Guid signerId, DateTimeOffset now)
    {
        var signer = EnsureSignerCanAct(signerId, now);
        signer.MarkSigned(now);
        Raise(new DomainEvent("signer.signed", signer.Email, null));

        if (_signers.TrueForAll(s => s.Status == SignerStatus.Signed))
        {
            Status = DocumentStatus.Completed;
            ClosedAt = now;
            Raise(new DomainEvent("document.completed", "system", null, NotificationKind.DocumentCompleted, OwnerEmail));
            return;
        }

        RequestNextSignature();
    }

    public void Decline(Guid signerId, string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("reason_required", "A reason is required to decline.");
        }

        var signer = EnsureSignerCanAct(signerId, now);
        signer.MarkDeclined(reason.Trim());
        Status = DocumentStatus.Declined;
        ClosedAt = now;
        Raise(new DomainEvent("signer.declined", signer.Email, reason.Trim(), NotificationKind.DocumentDeclined, OwnerEmail));
    }

    public bool TryExpire(DateTimeOffset now)
    {
        if (Status != DocumentStatus.Sent || ExpiresAt is null || now < ExpiresAt)
        {
            return false;
        }

        Status = DocumentStatus.Expired;
        ClosedAt = now;
        Raise(new DomainEvent("document.expired", "system", null, NotificationKind.DocumentExpired, OwnerEmail));
        return true;
    }

    public Signer? CurrentSigner =>
        Status == DocumentStatus.Sent
            ? _signers.Where(s => s.Status == SignerStatus.Pending).MinBy(s => s.Order)
            : null;

    private Signer EnsureSignerCanAct(Guid signerId, DateTimeOffset now)
    {
        if (Status == DocumentStatus.Sent && ExpiresAt <= now)
        {
            throw new DomainException("document_expired", "The signing window has closed.");
        }

        if (Status != DocumentStatus.Sent)
        {
            throw new DomainException("not_in_signing", $"Document is {Status}, not open for signing.");
        }

        var signer = FindSigner(signerId);
        if (signer.Status != SignerStatus.Pending)
        {
            throw new DomainException("already_acted", "This signer has already acted.");
        }

        if (CurrentSigner?.Id != signer.Id)
        {
            throw new DomainException("not_your_turn", "Signers act in order; an earlier signer has not signed yet.");
        }

        return signer;
    }

    private void RequestNextSignature()
    {
        var next = CurrentSigner;
        if (next is not null)
        {
            Raise(new DomainEvent("signature.requested", "system", next.Email, NotificationKind.SignatureRequested, next.Email));
        }
    }

    private Signer FindSigner(Guid signerId) =>
        _signers.Find(s => s.Id == signerId)
        ?? throw new DomainException("signer_not_found", "Signer not found on this document.");

    private void EnsureDraft()
    {
        if (Status != DocumentStatus.Draft)
        {
            throw new DomainException("not_draft", $"Document is {Status}; only drafts can be edited.");
        }
    }

    private void Raise(DomainEvent e) => _events.Add(e);

    private static string NormalizeEmail(string email)
    {
        var trimmed = email?.Trim().ToLowerInvariant() ?? string.Empty;
        var at = trimmed.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at != trimmed.LastIndexOf('@') || at == trimmed.Length - 1 || trimmed.Contains(' ', StringComparison.Ordinal))
        {
            throw new DomainException("invalid_email", $"'{email}' is not a valid email address.");
        }

        return trimmed;
    }
}
