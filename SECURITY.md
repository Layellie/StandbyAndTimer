# Security policy

StandbyAndTimer runs **with Administrator privileges** and makes direct calls
into `ntdll` (`NtSetTimerResolution`, `NtSetSystemInformation`) and the
Windows scheduler. A vulnerability here is more impactful than in a typical
user-mode app, so I treat security reports seriously and prioritize them.

## Supported versions

Only the **latest published release** receives security fixes. The version is
visible in the Settings panel footer of the app, and on the
[Releases page](https://github.com/Layellie/StandbyAndTimer/releases).

Older 2.0.x releases that have been marked **pre-release** on GitHub are
superseded interim builds — they will not receive backported fixes; please
upgrade.

## How to report

Please **do not open a public issue or pull request** for security problems.

Use GitHub's private security advisory flow:

1. Go to <https://github.com/Layellie/StandbyAndTimer/security/advisories/new>
2. Fill in the report (a brief reproducer is enough — proof-of-concept code
   is welcome but not required).

If GitHub advisories aren't an option for you, email **sametkasmer16@gmail.com**
with subject prefix `[SECURITY] StandbyAndTimer` and I'll respond within
seven days to coordinate a fix.

## What to include

- Affected version (from the Settings panel footer).
- Windows build (`winver`).
- A short reproducer or scenario.
- Your assessment of impact (local privilege escalation, denial of service,
  information disclosure, etc.).

## What to expect from me

- **Acknowledgement** of receipt within 7 days.
- **A triage decision** (accepted, needs-info, won't-fix-and-why) within
  14 days of full reproducer.
- **A patched release** for accepted reports as fast as I can build and
  publish one; for non-trivial issues I'll coordinate a disclosure date
  with you.
- **Credit** in the release notes if you'd like — let me know how to attribute.

Thank you for helping keep StandbyAndTimer's users safe.
