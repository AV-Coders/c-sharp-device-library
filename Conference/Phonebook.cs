using AVCoders.Core;

namespace AVCoders.Conference;

public delegate void PhonebookRequestStatusChangedHandler(PhonebookRequestStatus status);

public enum PhonebookRequestStatus
{
    NotBegun,
    Idle,
    Downloading,
    Waiting,
    Error,
    Complete
}

public record PhonebookBase(string Name);

public record PhonebookNumber(string Number);

public record PhonebookFolder(string Name, List<PhonebookBase> Items, PhonebookRequestStatus ContentDownloadState): PhonebookBase(Name)
{
    public PhonebookRequestStatus ContentDownloadState { get; set; }
}

public record PhonebookContact(string Name, List<PhonebookNumber> Numbers): PhonebookBase(Name);

public delegate void PhonebookUpdated(PhonebookFolder folder);

public abstract class PhonebookParserBase : DeviceBase
{
    private PhonebookRequestStatus _phonebookRequestStatus = PhonebookRequestStatus.Idle;

    public PhonebookRequestStatusChangedHandler PhonebookRequestStatusChangedHandlers;
    public PhonebookUpdated? PhonebookUpdated { get; set; }
    public PhonebookFolder PhoneBook { get; init; }

    public PhonebookRequestStatus PhonebookRequestStatus
    {
        get => _phonebookRequestStatus;
        protected set
        {
            if (_phonebookRequestStatus == value)
                return;
            _phonebookRequestStatus = value;
            PhonebookRequestStatusChangedHandlers?.Invoke(value);
        }
    }

    protected PhonebookParserBase(PhonebookFolder folder)
    {
        PhoneBook = folder;
    }

    public void RequestPhonebook()
    {
        PhonebookRequestStatus = PhonebookRequestStatus.Waiting;
        DoRequestPhonebook();
    }
    protected abstract void DoRequestPhonebook();

    public override void PowerOn() => DoRequestPhonebook();
    public override void PowerOff() => DoRequestPhonebook();
}