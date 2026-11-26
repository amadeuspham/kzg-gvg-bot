import { execSync } from 'node:child_process'
import { existsSync, statSync } from 'node:fs'
import { dirname, relative, resolve, sep } from 'node:path'
import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vitepress'

const docsDir = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const repoRoot = resolve(docsDir, '..')

// Working notes that live in the repo but are not published (kept in sync with srcExclude below).
const unpublished = ['plan.md', 'decisions.md']

// "owner/repo" comes from the workflow that builds the site (GITHUB_REPOSITORY), so no account is committed.
// Local builds fall back to the git remote.
function repository(): string {
  const fromEnv = process.env.GITHUB_REPOSITORY
  if (fromEnv) return fromEnv
  try {
    const remote = execSync('git config --get remote.origin.url', { cwd: repoRoot, encoding: 'utf8' }).trim()
    const match = /github\.com[:/]([^/]+\/[^/]+?)(?:\.git)?$/.exec(remote)
    if (match) return match[1]
  } catch {
    // fall through to the error below
  }
  throw new Error('Set GITHUB_REPOSITORY=owner/repo, or run inside a clone with a github.com origin.')
}

const repo = repository()
const repoName = repo.split('/')[1]
const githubUrl = `https://github.com/${repo}`

// A relative link that leaves the published pages (source files, AGENTS.md, the unpublished notes) would 404 on the
// site, so point it at the file on GitHub instead. The Markdown keeps plain relative links so it still works in
// the GitHub UI and in editors.
function configureMarkdown(md: import('markdown-it').default) {
  // Inline code such as a Go template ({{.State.OOMKilled}}) must not be read as a Vue expression.
  const inlineCode = md.renderer.rules.code_inline!
  md.renderer.rules.code_inline = (tokens, idx, options, env, self) =>
    inlineCode(tokens, idx, options, env, self).replace('<code', '<code v-pre')

  md.core.ruler.push('repo-links', (state) => {
    const from = state.env?.path as string | undefined
    if (!from) return
    for (const block of state.tokens) {
      for (const token of block.children ?? []) {
        if (token.type !== 'link_open') continue
        const href = token.attrGet('href')
        if (!href || /^([a-z][a-z0-9+.-]*:|#|\/)/i.test(href)) continue
        const [path, hash] = href.split('#')
        const target = resolve(dirname(from), decodeURIComponent(path))
        const inDocs = target.startsWith(docsDir + sep)
        const rel = relative(docsDir, target)
        if (inDocs && !unpublished.includes(rel)) continue
        if (!existsSync(target)) continue
        const kind = statSync(target).isDirectory() ? 'tree' : 'blob'
        const repoPath = relative(repoRoot, target).split(sep).join('/')
        token.attrSet('href', `${githubUrl}/${kind}/main/${repoPath}${hash ? `#${hash}` : ''}`)
      }
    }
  })
}

export default defineConfig({
  title: 'Discord Broadcast',
  description: 'A Discord relay for team voice events: guides for players, hosts and developers.',
  // The download page owns the site root; the docs live under /docs/ next to it.
  base: `/${repoName}/docs/`,
  cleanUrls: true,
  srcExclude: unpublished,
  markdown: { config: configureMarkdown },
  themeConfig: {
    // logoLink is not prefixed with base, so spell out the site root (the download page), one level above the docs. An explicit target makes it a full page
    // load: the client router would otherwise look for a docs page.
    logoLink: { link: `/${repoName}/`, target: '_self' },
    nav: [
      { text: 'Players', link: '/guide/getting-started' },
      { text: 'Hosts', link: '/guide/hosting' },
      { text: 'Developers', link: '/guide/development' },
      { text: 'Reference', link: '/reference/bot' },
    ],
    sidebar: [
      {
        text: 'Guide',
        items: [
          { text: 'Overview', link: '/' },
          { text: 'Install Discord Broadcast', link: '/guide/getting-started' },
          { text: 'Host the bot', link: '/guide/hosting' },
          { text: 'Developer setup', link: '/guide/development' },
        ],
      },
      {
        text: 'Reference',
        items: [
          { text: 'Bot reference', link: '/reference/bot' },
          { text: 'Contracts', link: '/reference/contracts' },
        ],
      },
    ],
    socialLinks: [{ icon: 'github', link: githubUrl }],
    editLink: {
      pattern: `${githubUrl}/edit/main/docs/:path`,
      text: 'Edit this page on GitHub',
    },
    search: { provider: 'local' },
  },
})
