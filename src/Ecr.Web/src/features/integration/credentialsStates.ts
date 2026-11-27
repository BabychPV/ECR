/** Ключі відмов, після яких єдина дія — ввести пароль заново (стани «не читається» / «відхилено»). */
export const CredentialsStateKeys: Readonly<Record<string, string>> = {
  'err.ECR-INT-0502.credentialsRejected': 'sources.credentialsRejected',
  'err.ECR-INT-0502.credentialsUnreadable': 'sources.credentialsUnreadable',
  'integration.test.failed.credentialsRejected': 'sources.credentialsRejected',
  'integration.test.failed.credentialsUnreadable': 'sources.credentialsUnreadable',
};
