import type { AccountInfo, PublicClientApplication } from '@azure/msal-browser';
import type { EntraConfig } from '../../../environments/runtime-config';

/**
 * The only file that touches MSAL.
 *
 * Loaded with a dynamic import, so a demo or offline build never downloads
 * the library. Authorization code + PKCE through a full-page redirect: popups
 * are blocked on the front-desk tablets, and a redirect leaves the token
 * handling to one place — `complete()` on the way back in.
 *
 * Tokens live in sessionStorage: closing the tab signs the operator out of
 * the workspace, which is what a shared reception terminal needs.
 */
export class EntraClient {
  private constructor(
    private readonly pca: PublicClientApplication,
    private readonly config: EntraConfig,
  ) {}

  static async create(config: EntraConfig): Promise<EntraClient> {
    const msal = await import('@azure/msal-browser');
    const pca = new msal.PublicClientApplication({
      auth: {
        clientId: config.clientId,
        authority: config.authority,
        redirectUri: config.redirectUri ?? window.location.origin + '/sign-in',
        postLogoutRedirectUri: window.location.origin + '/sign-in',
      },
      cache: { cacheLocation: msal.BrowserCacheLocation.SessionStorage },
    });
    await pca.initialize();
    return new EntraClient(pca, config);
  }

  /**
   * Finishes a redirect sign-in if this page load is one. The account is the
   * one signed in (fresh or cached); `returnUrl` is set only straight after a
   * redirect.
   */
  async complete(): Promise<{ account: AccountInfo | null; returnUrl: string | null }> {
    const result = await this.pca.handleRedirectPromise();
    const account = result?.account ?? this.pca.getActiveAccount() ?? this.pca.getAllAccounts()[0] ?? null;
    if (account) this.pca.setActiveAccount(account);
    return { account, returnUrl: result ? EntraClient.returnUrlFrom(result.state) : null };
  }

  /** Leaves the page. `state` comes back through `complete()` untouched. */
  signIn(returnUrl: string): Promise<void> {
    return this.pca.loginRedirect({ scopes: [...this.config.apiScopes], state: returnUrl, prompt: 'select_account' });
  }

  signOut(): Promise<void> {
    return this.pca.logoutRedirect({ account: this.pca.getActiveAccount() ?? undefined });
  }

  /**
   * An access token for the API. Silent first; an expired session that needs
   * interaction redirects, which is why this can leave the page.
   */
  async accessToken(): Promise<string> {
    const account = this.pca.getActiveAccount();
    if (!account) throw new Error('No Entra account is signed in.');
    const msal = await import('@azure/msal-browser');
    try {
      const r = await this.pca.acquireTokenSilent({ scopes: [...this.config.apiScopes], account });
      return r.accessToken;
    } catch (err) {
      if (err instanceof msal.InteractionRequiredAuthError) {
        await this.pca.acquireTokenRedirect({ scopes: [...this.config.apiScopes], account });
      }
      throw err;
    }
  }

  /** The state passed to signIn, recovered after the redirect. */
  static returnUrlFrom(state: string | undefined): string {
    return state && state.startsWith('/') && !state.startsWith('//') ? state : '/app';
  }
}
