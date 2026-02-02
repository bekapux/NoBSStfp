using System.Text.Json.Serialization;
using System.Collections.Generic;
using NoBSSftp.Models;

namespace NoBSSftp.Models;

[JsonSerializable(typeof(List<ServerProfile>))]
[JsonSerializable(typeof(List<ServerFolder>))]
[JsonSerializable(typeof(ServerLibrary))]
public partial class SerializationContext : JsonSerializerContext
{
}
