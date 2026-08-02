# S8 Capacity / HIL Readiness

This directory is simulation-only. It may compose S1-S7 virtual runtimes and evidence contracts, but it must never depend on real PLC/S7/Snap7 clients, sockets, production HTTP/SQL clients, production dispatch/control services, or real model inference.

The 8-hour and 24-hour profiles are accelerated virtual-time endurance scenarios. Passing them means the repository is software-ready to enter S9 HIL; it does not mean HIL, mechanical safety, industrial-network, protocol, site, or production acceptance has passed.
