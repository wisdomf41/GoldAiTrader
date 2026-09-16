# Code of Conduct

## Our Commitment

We are committed to making participation in the GoldAiTrader project a respectful, inclusive, and professional experience for everyone.

Contributors, maintainers, reviewers, and users are expected to communicate constructively and collaborate in good faith.

## Expected Behavior

Examples of positive behavior include:

- being respectful and professional in discussions
- giving constructive technical feedback
- focusing criticism on ideas, code, and design rather than individuals
- being open to different viewpoints and approaches
- documenting decisions clearly
- respecting project safety requirements
- helping other contributors understand the architecture and contribution process

## Unacceptable Behavior

Unacceptable behavior includes:

- harassment, threats, or personal attacks
- discriminatory or degrading comments
- deliberate disruption of discussions or reviews
- publishing another person's private information without permission
- intentionally introducing malicious code, hidden behavior, or unsafe execution paths
- knowingly bypassing security, risk-management, or trading-safety controls
- submitting secrets, credentials, API keys, tokens, or other sensitive information to the repository
- other conduct that would reasonably be considered inappropriate in a professional software project

## Trading and Security Responsibility

GoldAiTrader includes components related to trading execution, authentication, risk management, and broker integration.

Contributors must not intentionally introduce changes that:

- enable live trading without explicit authorization
- bypass configured risk controls
- remove mandatory stop-loss protections
- weaken authentication or replay protection
- expose credentials or secrets
- bypass execution ownership or reconciliation rules
- treat uncertain execution outcomes as safe to retry without reconciliation

Safety-sensitive changes should be clearly explained and supported by appropriate tests.

## Enforcement

Project maintainers are responsible for clarifying and enforcing these standards.

Instances of abusive, unsafe, or otherwise unacceptable behavior may result in comments, issues, pull requests, or contributions being edited, rejected, locked, or removed.

Repeated or serious violations may result in temporary or permanent restrictions from participating in the project.

## Reporting

If you experience or observe behavior that violates this Code of Conduct, report it privately to the repository maintainer through GitHub.

Security vulnerabilities should be reported according to the project's `SECURITY.md` policy rather than through a public issue.

Reports will be reviewed as promptly and fairly as reasonably possible.

## Scope

This Code of Conduct applies to all project spaces, including:

- issues
- pull requests
- code reviews
- discussions
- documentation contributions
- other public or private project-related communication

## Attribution

This Code of Conduct is adapted for the GoldAiTrader project and its engineering, security, and trading-safety requirements.