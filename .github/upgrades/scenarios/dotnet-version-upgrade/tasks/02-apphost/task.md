# 02-apphost: Upgrade Mulse.AppHost

Retarget `Mulse.AppHost` to `net11.0` and update its Aspire packages, including `Aspire.Hosting.AppHost` from 13.6.4 to the assessment-recommended 13.6.5. The assessment flags this package for a security vulnerability. Keep its references to `Mulse.Ui` and `Service` working while those projects still target `net10.0`.

**Done when**: `Mulse.AppHost` builds for `net11.0`, its package vulnerability finding is addressed, and its project references resolve.
