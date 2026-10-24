# GitHub Pages and custom-domain HTTPS

The product site is the static content under `site/`. `.github/workflows/pages.yml` validates it, creates a one-day GitHub Pages deployment artifact, and deploys that artifact to the `github-pages` environment after a push to `main` or a manual run. Pull requests validate the site but upload nothing.

The Pages bundle is deployment transport only. It is not a desktop release artifact, is not attached to a GitHub Release, and must never contain application packages, test output, logs, or repository files outside `site/`.

## One-time repository setup

1. Open **Settings → Pages** in `nullabletype/dot-orbit`.
2. Under **Build and deployment**, set **Source** to **GitHub Actions**.
3. Keep the custom domain as `dot-orbit.app`. The checked-in `site/CNAME` preserves it across deployments.
4. Merge or manually run the Pages workflow and wait for the `github-pages` environment deployment to finish.

The workflow requires `pages: write` and `id-token: write` only in its deployment job. The validation job has read-only repository access.

## Cloudflare DNS

For the apex domain, keep these four `A` records and remove conflicting apex `A`, `AAAA`, or `CNAME` records:

| Name | Type | Content |
| --- | --- | --- |
| `@` | `A` | `185.199.108.153` |
| `@` | `A` | `185.199.109.153` |
| `@` | `A` | `185.199.110.153` |
| `@` | `A` | `185.199.111.153` |

An optional `www` record can be a `CNAME` to `nullabletype.github.io`. Keep the records **DNS only** (grey cloud) while GitHub validates the domain and provisions its certificate. If the zone uses restrictive `CAA` records, allow `letsencrypt.org` or remove the conflicting restriction so GitHub can issue the certificate.

GitHub's DNS check proves that the name resolves correctly; it does not mean the TLS certificate is ready. Certificate provisioning can take up to 24 hours after the domain and DNS are correct. During that period **Enforce HTTPS** can remain unavailable.

## Enable HTTPS

1. Confirm the Pages deployment completed successfully.
2. Confirm `https://dot-orbit.app/` serves the deployed site without a certificate warning.
3. Return to **Settings → Pages** and select **Enforce HTTPS** when the option becomes available.
4. If Cloudflare proxying is wanted afterwards, enable it only after GitHub's origin certificate works. Set Cloudflare SSL/TLS mode to **Full (strict)**, not **Flexible**, and avoid overlapping redirect rules that can create loops.

The current Pages state can also be inspected without changing it:

```sh
gh api repos/nullabletype/dot-orbit/pages \
  --jq '{html_url, cname, build_type, https_enforced, status}'
```

If the certificate is still unavailable after 24 hours, recheck the exact records, Cloudflare proxy status, and `CAA` policy. GitHub's documented last-resort reset is to remove the custom domain from Pages, save, add it again, and wait for provisioning to restart. Do not repeatedly remove and re-add it while issuance is in progress.

Sources: [GitHub custom-domain setup](https://docs.github.com/en/pages/configuring-a-custom-domain-for-your-github-pages-site/managing-a-custom-domain-for-your-github-pages-site), [GitHub Pages HTTPS](https://docs.github.com/en/pages/getting-started-with-github-pages/securing-your-github-pages-site-with-https), [Cloudflare proxy status](https://developers.cloudflare.com/dns/proxy-status/), and [Cloudflare SSL modes](https://developers.cloudflare.com/ssl/origin-configuration/ssl-modes/).
