namespace AVCoders.Conference;

public record PhonebookBase(string Name);

public record PhonebookNumber(string Number);

public record PhonebookFolder(string Name, List<PhonebookBase> Items): PhonebookBase(Name);

public record PhonebookContact(string Name, List<PhonebookNumber> Numbers): PhonebookBase(Name);