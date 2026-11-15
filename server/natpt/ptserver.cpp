// Minimal RakNet NatPunchthroughServer (the plugin of RakNet's NATCompleteServer sample, nothing else). Usage: ptserver [port=10000]
#include "RakPeerInterface.h"
#include "RakSleep.h"
#include "MessageIdentifiers.h"
#include "NatPunchthroughServer.h"
#include <stdio.h>
#include <stdlib.h>
using namespace RakNet;
int main(int argc, char **argv)
{
	unsigned short port = argc > 1 ? (unsigned short)atoi(argv[1]) : 10000;
	RakPeerInterface *peer = RakPeerInterface::GetInstance();
	NatPunchthroughServer nps;
	NatPunchthroughServerDebugInterface_Printf dbg;
	nps.SetDebugInterface(&dbg);
	peer->AttachPlugin(&nps);
	SocketDescriptor sd(port, 0);
	StartupResult r = peer->Startup(1024, &sd, 1);
	if (r != RAKNET_STARTED) { printf("startup failed: %d\n", (int)r); return 1; }
	peer->SetMaximumIncomingConnections(1024);
	printf("NatPunchthroughServer on UDP %u, guid %s\n", port, peer->GetMyGUID().ToString());
	fflush(stdout);
	for (;;)
	{
		for (Packet *p = peer->Receive(); p; peer->DeallocatePacket(p), p = peer->Receive())
		{
			if (p->data[0] >= 134) continue;  // the game also sends its own message ids (134 and up) to every peer, this one included
			printf("id %u from %s guid %s\n", (unsigned)p->data[0], p->systemAddress.ToString(true), p->guid.ToString());
			fflush(stdout);
		}
		RakSleep(30);
	}
}
