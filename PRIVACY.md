# Privacy

The launcher stores a nickname, selected server, installation directory,
maximum memory and minimize preference locally. It does not request Minecraft
passwords, server `/login` passwords, Microsoft credentials or administrator
passwords. There is no analytics SDK or advertising.

On opening the launcher or changing server selection it requests public server
status and a signed catalog over HTTPS to check the launcher version. Clicking
Update downloads the new launcher EXE after confirmation. On Play or Verify Files
it requests a signed catalog,
the selected manifest and the missing/changed objects. The site's operator
can see normal network metadata (such as IP address and HTTP user agent) in
server access logs. A server-status request happens automatically at startup.

Minecraft is a separate process. When the user connects to a game server, that
server receives the game connection and nickname, and the game/mods may have
their own network behavior. They are not part of this source repository.

Game output and errors are stored locally for diagnosis. They can contain
nicknames, server addresses, local paths, and chat text. Review and redact logs
before sharing them; they are not automatically uploaded by the launcher.

Deleting local launcher settings removes saved preferences and the trusted
catalog cache. Client worlds/settings remain in their selected directories
until the user deletes them.
