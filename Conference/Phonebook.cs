using AVCoders.Core;

namespace AVCoders.Conference;

public record PhonebookBase(string Name);

public record PhonebookNumber(string Number);

public record PhonebookFolder(string Name, List<PhonebookBase> Items): PhonebookBase(Name);

public record PhonebookContact(string Name, List<PhonebookNumber> Numbers): PhonebookBase(Name);

public delegate void PhonebookUpdated(PhonebookFolder folder);

public abstract class PhonebookParserBase : LogBase
{
    protected PhonebookParserBase(string name) : base(name)
    {
    }

    public PhonebookUpdated? PhonebookUpdated { get; set; }
}