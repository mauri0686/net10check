using System.Runtime.Serialization.Formatters.Binary;
public class Cache
{
    // var old = new BinaryFormatter(); in a comment must not count
    public object Load(Stream s) => new BinaryFormatter().Deserialize(s);
}
