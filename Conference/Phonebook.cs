using AVCoders.Core;

namespace AVCoders.Conference;

public record PhonebookBase(string Name);

public record PhonebookNumber(string Number);

public record PhonebookFolder(string Name, List<PhonebookBase> Items, bool ContentsFetched): PhonebookBase(Name)
{
    public bool ContentsFetched { get; set; }
}

public record PhonebookContact(string Name, List<PhonebookNumber> Numbers): PhonebookBase(Name);

public delegate void PhonebookUpdated(PhonebookFolder folder);

public abstract class PhonebookParserBase : DeviceBase
{
    public PhonebookUpdated? PhonebookUpdated { get; set; }
    public PhonebookFolder PhoneBook { get; init; }

    protected PhonebookParserBase(PhonebookFolder folder)
    {
        PhoneBook = folder;
    }

    public void RequestPhonebook()
    {
        DoRequestPhonebook();
    }
    protected abstract void DoRequestPhonebook();

    public override void PowerOn() => DoRequestPhonebook();
    public override void PowerOff() => DoRequestPhonebook();
}