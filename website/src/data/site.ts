// Facts about the project that pages link to. One place to change when the repository moves.
const repo = 'https://github.com/4a11/factorio-mod-studio';

export const site = {
  name: 'Factorio Mod Studio',
  shortName: 'FMStudio',
  repo,
  releases: `${repo}/releases`,
  latestRelease: `${repo}/releases/latest`,
  issues: `${repo}/issues`,
  discussions: `${repo}/discussions`,
  bugReport: `${repo}/issues/new?template=bug_report.yml`,
  featureRequest: `${repo}/issues/new?template=feature_request.yml`,
  contributing: `${repo}/blob/main/CONTRIBUTING.md`,
  security: `${repo}/blob/main/SECURITY.md`,
  securityReport: `${repo}/security/advisories/new`,
  codeOfConduct: `${repo}/blob/main/CODE_OF_CONDUCT.md`,
  roadmap: `${repo}#roadmap`,
  translating: `${repo}#translating-the-interface`,
  license: `${repo}/blob/main/LICENSE`,
} as const;
