using System.Text.Json.Serialization;
using System.Collections.Generic;
using NoBSSftp.Models;

namespace NoBSSftp.Models;

[JsonSerializable(typeof(List<ServerProfile>))]
[JsonSerializable(typeof(List<ServerFolder>))]
[JsonSerializable(typeof(List<TrustedHostKeyEntry>))]
[JsonSerializable(typeof(ServerLibrary))]
[JsonSerializable(typeof(CredentialSecrets))]
[JsonSerializable(typeof(Dictionary<string, CredentialSecrets>))]
public partial class SerializationContext : JsonSerializerContext
{
}
