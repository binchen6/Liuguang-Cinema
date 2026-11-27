package tv.engine;

import android.content.Context;
import com.github.catvod.spider.Proxy;
import com.googlecode.d2j.dex.Dex2jar;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.net.URI;
import java.net.URL;
import java.net.URLClassLoader;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.Enumeration;
import java.util.HashMap;
import java.util.HashSet;
import java.util.HexFormat;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.TimeUnit;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;
import java.util.zip.ZipOutputStream;

/**
 * Dynamically downloads, DEX-to-JVM translates (via Apache-2.0 dex2jar), caches,
 * and loads external Android TVBox spider JARs in isolated child-first ClassLoaders.
 */
final class ExternalJarLoader {
    private static final long MAX_JAR_BYTES = 32L * 1024 * 1024;
    private static final String[] GITHUB_MIRRORS = {
        "https://gh-proxy.com/",
        "https://cdn.gh-proxy.org/",
        "https://edgeone.gh-proxy.org/",
        "https://gh-proxy.org/",
        "https://ghfast.top/"
    };

    private static final OkHttpClient HTTP = new OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS)
        .readTimeout(45, TimeUnit.SECONDS)
        .followRedirects(true)
        .followSslRedirects(true)
        .build();

    private final File cacheRoot;
    private final DesktopApplication app;
    private final Map<String, LoadedJar> loadedJars = new ConcurrentHashMap<>();
    private final Map<String, Object> jarLocks = new ConcurrentHashMap<>();

    ExternalJarLoader(DesktopApplication app) {
        this.app = app;
        this.cacheRoot = new File(app.getFilesDir(), "jar-cache");
        new File(cacheRoot, "raw").mkdirs();
        new File(cacheRoot, "raw-origin").mkdirs();
        new File(cacheRoot, "jvm").mkdirs();
    }

    ReflectiveSpider createSpider(String jarSpec, String api) throws Exception {
        LoadedJar jar = loadJar(jarSpec);
        Class<?> spiderClass = jar.resolveSpiderClass(api);
        if (spiderClass == null) {
            throw new ClassNotFoundException("Spider class not found in external jar for " + api);
        }
        Object instance = spiderClass.getDeclaredConstructor().newInstance();
        return new ReflectiveSpider(instance, jar.classLoader);
    }

    private LoadedJar loadJar(String rawSpec) throws Exception {
        ParsedSpec spec = parseSpec(rawSpec);
        String cacheKey = sha256Hex(spec.url + "|" + spec.md5);
        LoadedJar existing = loadedJars.get(cacheKey);
        if (existing != null) return existing;

        Object lock = jarLocks.computeIfAbsent(cacheKey, k -> new Object());
        synchronized (lock) {
            existing = loadedJars.get(cacheKey);
            if (existing != null) return existing;

            File rawFile = obtainRawJar(spec, cacheKey);
            String contentHash = sha256File(rawFile);
            File jvmJar = ensureJvmJar(rawFile, contentHash);
            Set<String> classNames = indexSpiderClasses(jvmJar);

            SpiderClassLoader loader = new SpiderClassLoader(
                new URL[]{jvmJar.toURI().toURL()},
                ExternalJarLoader.class.getClassLoader());

            ClassLoader prev = Thread.currentThread().getContextClassLoader();
            Thread.currentThread().setContextClassLoader(loader);
            try {
                tryInitExternalJar(loader);
                tryRegisterExternalProxy(loader);
            } finally {
                Thread.currentThread().setContextClassLoader(prev);
            }

            LoadedJar loaded = new LoadedJar(loader, classNames);
            loadedJars.put(cacheKey, loaded);
            return loaded;
        }
    }

    private void tryInitExternalJar(ClassLoader loader) {
        try {
            Class<?> initClass = loader.loadClass("com.github.catvod.spider.Init");
            try {
                Method init = initClass.getDeclaredMethod("init", Context.class);
                init.setAccessible(true);
                init.invoke(null, app);
                return;
            } catch (NoSuchMethodException ignored) { }
            try {
                Method init = initClass.getDeclaredMethod("init", android.app.Application.class);
                init.setAccessible(true);
                init.invoke(null, app);
            } catch (NoSuchMethodException ignored) { }
        } catch (Throwable ignored) { }
    }

    private void tryRegisterExternalProxy(ClassLoader loader) {
        try {
            Class<?> proxyClass = loader.loadClass("com.github.catvod.spider.Proxy");
            Proxy.registerExternalProxy(proxyClass);
        } catch (Throwable ignored) { }
    }

    private File obtainRawJar(ParsedSpec spec, String cacheKey) throws IOException {
        if (spec.url.startsWith("file://")) {
            File local = new File(URI.create(spec.url));
            if (!local.isFile()) throw new IOException("Local spider jar not found: " + local);
            if (!spec.md5.isEmpty() && !spec.md5.equalsIgnoreCase(md5File(local))) {
                throw new IOException("Local spider JAR checksum does not match the source configuration");
            }
            return local;
        }
        File cached = new File(new File(cacheRoot, spec.md5.isEmpty() ? "raw-origin" : "raw"), cacheKey + ".jar");
        if (cached.isFile() && cached.length() > 128) {
            if (spec.md5.isEmpty() || spec.md5.equalsIgnoreCase(md5File(cached))) {
                return cached;
            }
        }
        List<String> candidates = buildDownloadCandidates(spec);
        IOException lastError = null;
        for (String candidate : candidates) {
            try {
                byte[] bytes = downloadBytes(candidate);
                if (!isZipArchive(bytes)) {
                    throw new IOException("Downloaded spider is not a valid JAR/ZIP archive: " + candidate);
                }
                if (!spec.md5.isEmpty() && !spec.md5.equalsIgnoreCase(md5Bytes(bytes)))
                    throw new IOException("Spider JAR checksum does not match the source configuration");
                File tmp = new File(cached.getParentFile(), cached.getName() + "." + System.nanoTime() + ".tmp");
                try {
                    Files.write(tmp.toPath(), bytes);
                    Files.move(tmp.toPath(), cached.toPath(), StandardCopyOption.REPLACE_EXISTING);
                } finally {
                    Files.deleteIfExists(tmp.toPath());
                }
                return cached;
            } catch (IOException ex) {
                lastError = ex;
            }
        }
        throw lastError != null ? lastError : new IOException("Failed to download spider jar: " + spec.url);
    }

    private static List<String> buildDownloadCandidates(ParsedSpec spec) {
        List<String> list = new ArrayList<>();
        list.add(spec.url);
        String lower = spec.url.toLowerCase(Locale.ROOT);
        if (!spec.md5.isEmpty() &&
                (lower.startsWith("https://raw.githubusercontent.com/") || lower.startsWith("https://github.com/"))) {
            for (String mirror : GITHUB_MIRRORS) {
                list.add(mirror + spec.url);
            }
        }
        return list;
    }

    private static byte[] downloadBytes(String url) throws IOException {
        Request request = new Request.Builder()
            .url(url)
            .header("User-Agent", "okhttp/4.12.0")
            .build();
        try (Response response = HTTP.newCall(request).execute()) {
            if (!response.isSuccessful() || response.body() == null) {
                throw new IOException("HTTP " + response.code() + " downloading " + url);
            }
            long len = response.body().contentLength();
            if (len > MAX_JAR_BYTES) throw new IOException("Spider JAR exceeds 32 MB limit");
            try (InputStream in = response.body().byteStream();
                 ByteArrayOutputStream out = new ByteArrayOutputStream()) {
                byte[] buf = new byte[16384];
                int read;
                long total = 0;
                while ((read = in.read(buf)) != -1) {
                    total += read;
                    if (total > MAX_JAR_BYTES) throw new IOException("Spider JAR exceeds 32 MB limit");
                    out.write(buf, 0, read);
                }
                return out.toByteArray();
            }
        }
    }

    private File ensureJvmJar(File rawFile, String contentHash) throws IOException {
        List<String> dexEntries = new ArrayList<>();
        boolean hasClassFiles = false;
        try (ZipFile zip = new ZipFile(rawFile)) {
            Enumeration<? extends ZipEntry> entries = zip.entries();
            while (entries.hasMoreElements()) {
                String name = entries.nextElement().getName();
                if (name.endsWith(".dex") && !name.contains("/")) dexEntries.add(name);
                else if (name.endsWith(".class")) hasClassFiles = true;
            }
        }
        if (dexEntries.isEmpty() && hasClassFiles) {
            return rawFile;
        }
        if (dexEntries.isEmpty()) {
            throw new IOException("Spider archive contains neither classes.dex nor JVM .class files");
        }

        File converted = new File(new File(cacheRoot, "jvm"), contentHash + ".jar");
        if (converted.isFile() && converted.length() > 128) {
            return converted;
        }

        File tmpOut = new File(converted.getParentFile(), converted.getName() + "." + System.nanoTime() + ".tmp");
        List<Path> tempDirs = new ArrayList<>();
        try (ZipFile sourceZip = new ZipFile(rawFile);
             ZipOutputStream outZip = new ZipOutputStream(new FileOutputStream(tmpOut))) {
            Set<String> written = new HashSet<>();

            // Translate every classes*.dex entry into JVM bytecode via Apache-2.0 dex2jar.
            // Output to a temp directory rather than a pre-created .jar file to avoid JDK 17/21
            // ZipFileSystem ReadOnlyFileSystemException inside Dex2jar.to(Path).
            for (String dexName : dexEntries) {
                ZipEntry dexEntry = sourceZip.getEntry(dexName);
                if (dexEntry == null) continue;
                byte[] dexBytes;
                try (InputStream in = sourceZip.getInputStream(dexEntry)) {
                    dexBytes = in.readAllBytes();
                }
                Path singleDir = Files.createTempDirectory(converted.getParentFile().toPath(), "d2j-");
                tempDirs.add(singleDir);
                Dex2jar.from(dexBytes).skipExceptions(true).to(singleDir);
                copyDirectoryEntries(singleDir, outZip, written);
            }

            // Copy original non-dex, non-signature resources (assets, properties, etc.)
            copyZipEntries(sourceZip, outZip, written, true);
        } finally {
            for (Path dir : tempDirs) {
                deleteDirectoryQuietly(dir);
            }
        }
        Files.move(tmpOut.toPath(), converted.toPath(), StandardCopyOption.REPLACE_EXISTING);
        return converted;
    }

    private static void copyDirectoryEntries(Path rootDir, ZipOutputStream dest, Set<String> written) throws IOException {
        try (var stream = Files.walk(rootDir)) {
            List<Path> files = stream.filter(Files::isRegularFile).toList();
            byte[] buf = new byte[16384];
            for (Path file : files) {
                String entryName = rootDir.relativize(file).toString().replace('\\', '/');
                if (!written.add(entryName)) continue;
                dest.putNextEntry(new ZipEntry(entryName));
                try (InputStream in = Files.newInputStream(file)) {
                    int read;
                    while ((read = in.read(buf)) != -1) dest.write(buf, 0, read);
                }
                dest.closeEntry();
            }
        }
    }

    private static void deleteDirectoryQuietly(Path dir) {
        try (var stream = Files.walk(dir)) {
            stream.sorted(java.util.Comparator.reverseOrder()).forEach(path -> {
                try { Files.deleteIfExists(path); } catch (IOException ignored) { }
            });
        } catch (IOException ignored) { }
    }

    private static void copyZipEntries(ZipFile source, ZipOutputStream dest, Set<String> written, boolean skipDexAndMetaSig) throws IOException {
        Enumeration<? extends ZipEntry> entries = source.entries();
        byte[] buf = new byte[16384];
        while (entries.hasMoreElements()) {
            ZipEntry entry = entries.nextElement();
            if (entry.isDirectory()) continue;
            String name = entry.getName();
            if (skipDexAndMetaSig) {
                if (name.endsWith(".dex") || name.startsWith("META-INF/") && (name.endsWith(".SF") || name.endsWith(".DSA") || name.endsWith(".RSA"))) {
                    continue;
                }
            }
            if (!written.add(name)) continue;
            dest.putNextEntry(new ZipEntry(name));
            try (InputStream in = source.getInputStream(entry)) {
                int read;
                while ((read = in.read(buf)) != -1) dest.write(buf, 0, read);
            }
            dest.closeEntry();
        }
    }

    private static Set<String> indexSpiderClasses(File jarFile) {
        Set<String> names = new HashSet<>();
        try (ZipFile zip = new ZipFile(jarFile)) {
            Enumeration<? extends ZipEntry> entries = zip.entries();
            while (entries.hasMoreElements()) {
                String name = entries.nextElement().getName();
                if (name.startsWith("com/github/catvod/spider/") && name.endsWith(".class") && !name.contains("$")) {
                    names.add(name.substring(0, name.length() - 6).replace('/', '.'));
                }
            }
        } catch (IOException ignored) { }
        return names;
    }

    private static ParsedSpec parseSpec(String raw) {
        String trimmed = raw == null ? "" : raw.trim();
        int semi = trimmed.indexOf(";md5;");
        if (semi > 0) {
            String url = trimmed.substring(0, semi).trim();
            String md5 = trimmed.substring(semi + 5).trim();
            int nextSemi = md5.indexOf(';');
            if (nextSemi >= 0) md5 = md5.substring(0, nextSemi).trim();
            return new ParsedSpec(url, md5);
        }
        int anySemi = trimmed.indexOf(';');
        if (anySemi > 0) trimmed = trimmed.substring(0, anySemi).trim();
        return new ParsedSpec(trimmed, "");
    }

    private static boolean isZipArchive(byte[] bytes) {
        return bytes != null && bytes.length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B;
    }

    private static String sha256Hex(String value) {
        try {
            MessageDigest md = MessageDigest.getInstance("SHA-256");
            return HexFormat.of().formatHex(md.digest(value.getBytes(StandardCharsets.UTF_8)));
        } catch (Exception ex) {
            throw new IllegalStateException(ex);
        }
    }

    private static String sha256File(File file) throws IOException {
        try {
            MessageDigest md = MessageDigest.getInstance("SHA-256");
            md.update(Files.readAllBytes(file.toPath()));
            return HexFormat.of().formatHex(md.digest());
        } catch (IOException ex) {
            throw ex;
        } catch (Exception ex) {
            throw new IOException(ex);
        }
    }

    private static String md5Bytes(byte[] bytes) {
        try {
            MessageDigest md = MessageDigest.getInstance("MD5");
            return HexFormat.of().formatHex(md.digest(bytes));
        } catch (Exception ex) {
            return "";
        }
    }

    private static String md5File(File file) {
        try { return md5Bytes(Files.readAllBytes(file.toPath())); }
        catch (IOException ex) { return ""; }
    }

    private record ParsedSpec(String url, String md5) { }

    private record LoadedJar(SpiderClassLoader classLoader, Set<String> spiderClasses) {
        Class<?> resolveSpiderClass(String api) throws ClassNotFoundException {
            String base = api.startsWith("csp_") ? api.substring(4) : api;
            List<String> candidates = new ArrayList<>();
            candidates.add("com.github.catvod.spider." + base);
            if (base.endsWith("Guard") && base.length() > 5) {
                candidates.add("com.github.catvod.spider." + base.substring(0, base.length() - 5));
            } else {
                candidates.add("com.github.catvod.spider." + base + "Guard");
            }
            for (String fqcn : candidates) {
                try { return classLoader.loadClass(fqcn); }
                catch (ClassNotFoundException ignored) { }
            }
            String lowerBase = base.toLowerCase(Locale.ROOT);
            String strippedBase = lowerBase.endsWith("guard") ? lowerBase.substring(0, lowerBase.length() - 5) : lowerBase;
            for (String existing : spiderClasses) {
                String simple = existing.substring("com.github.catvod.spider.".length()).toLowerCase(Locale.ROOT);
                if (simple.equals(lowerBase) || simple.equals(strippedBase)) {
                    return classLoader.loadClass(existing);
                }
            }
            return null;
        }
    }

    /**
     * Child-first ClassLoader isolating external Android TVBox JARs from built-in compiled spiders
     * while delegating JDK, Android stubs, and core runtime libraries to the parent ClassLoader.
     */
    private static final class SpiderClassLoader extends URLClassLoader {
        private static final String[] PARENT_FIRST_PREFIXES = {
            "java.", "javax.", "jdk.", "sun.", "com.sun.",
            "org.w3c.", "org.xml.", "org.ietf.", "org.jcp.",
            "android.", "androidx.",
            "okhttp3.", "okio.",
            "com.google.gson.", "org.json.",
            "org.jsoup.", "cn.wanghaomiao.",
            "fi.iki.elonen.",
            "org.apache.commons.",
            "org.bouncycastle.",
            "org.mozilla.universalchardet.",
            "com.google.zxing.",
            "tv.engine."
        };

        SpiderClassLoader(URL[] urls, ClassLoader parent) {
            super(urls, parent);
        }

        @Override
        protected Class<?> loadClass(String name, boolean resolve) throws ClassNotFoundException {
            synchronized (getClassLoadingLock(name)) {
                Class<?> loaded = findLoadedClass(name);
                if (loaded != null) return loaded;

                if (shouldDelegateToParent(name)) {
                    try {
                        loaded = getParent().loadClass(name);
                        if (resolve) resolveClass(loaded);
                        return loaded;
                    } catch (ClassNotFoundException ignored) { }
                }

                try {
                    loaded = findClass(name);
                    if (resolve) resolveClass(loaded);
                    return loaded;
                } catch (ClassNotFoundException ignored) {
                    loaded = getParent().loadClass(name);
                    if (resolve) resolveClass(loaded);
                    return loaded;
                }
            }
        }

        private static boolean shouldDelegateToParent(String name) {
            for (String prefix : PARENT_FIRST_PREFIXES) {
                if (name.startsWith(prefix)) return true;
            }
            return false;
        }
    }

    static final class ReflectiveSpider {
        private final Object instance;
        private final ClassLoader classLoader;

        ReflectiveSpider(Object instance, ClassLoader classLoader) {
            this.instance = instance;
            this.classLoader = classLoader;
        }

        void init(Context context, String ext) throws Exception {
            withContextLoader(() -> {
                if (invokeIfPresent("init", new Class<?>[]{Context.class, String.class}, context, ext != null ? ext : "")) return null;
                if (invokeIfPresent("init", new Class<?>[]{Context.class}, context)) return null;
                invokeIfPresent("init", new Class<?>[0]);
                return null;
            });
        }

        String homeContent(boolean filter) throws Exception {
            return withContextLoader(() -> {
                Object result = invoke("homeContent", new Class<?>[]{boolean.class}, filter);
                return result == null ? "" : String.valueOf(result);
            });
        }

        String homeVideoContent() throws Exception {
            return withContextLoader(() -> {
                Method m = findMethod("homeVideoContent");
                if (m == null) return "";
                Object result = invokeMethod(m);
                return result == null ? "" : String.valueOf(result);
            });
        }

        String categoryContent(String tid, String pg, boolean filter, HashMap<String, String> extend) throws Exception {
            return withContextLoader(() -> {
                Object result = invoke("categoryContent",
                    new Class<?>[]{String.class, String.class, boolean.class, HashMap.class},
                    tid, pg, filter, extend);
                return result == null ? "" : String.valueOf(result);
            });
        }

        String detailContent(List<String> ids) throws Exception {
            return withContextLoader(() -> {
                Object result = invoke("detailContent", new Class<?>[]{List.class}, ids);
                return result == null ? "" : String.valueOf(result);
            });
        }

        String searchContent(String keyword, boolean quick, String page) throws Exception {
            return withContextLoader(() -> {
                Method withPage = findMethod("searchContent", String.class, boolean.class, String.class);
                if (withPage != null && !withPage.getDeclaringClass().getSimpleName().equals("Spider")) {
                    Object res = invokeMethod(withPage, keyword, quick, page);
                    return res == null ? "" : String.valueOf(res);
                }
                if (page == null || page.isEmpty() || "1".equals(page)) {
                    Object res = invoke("searchContent", new Class<?>[]{String.class, boolean.class}, keyword, quick);
                    return res == null ? "" : String.valueOf(res);
                }
                return "{\"list\":[]}";
            });
        }

        String playerContent(String flag, String id, List<String> vipFlags) throws Exception {
            return withContextLoader(() -> {
                Object result = invoke("playerContent", new Class<?>[]{String.class, String.class, List.class}, flag, id, vipFlags);
                return result == null ? "" : String.valueOf(result);
            });
        }

        String liveContent(String url) throws Exception {
            return withContextLoader(() -> {
                Object result = invoke("liveContent", new Class<?>[]{String.class}, url);
                return result == null ? "" : String.valueOf(result);
            });
        }

        private <T> T withContextLoader(ThrowingSupplier<T> supplier) throws Exception {
            ClassLoader previous = Thread.currentThread().getContextClassLoader();
            Thread.currentThread().setContextClassLoader(classLoader);
            try {
                return supplier.get();
            } finally {
                Thread.currentThread().setContextClassLoader(previous);
            }
        }

        private boolean invokeIfPresent(String name, Class<?>[] types, Object... args) throws Exception {
            Method m = findMethod(name, types);
            if (m == null) return false;
            invokeMethod(m, args);
            return true;
        }

        private Object invoke(String name, Class<?>[] types, Object... args) throws Exception {
            Method m = findMethod(name, types);
            if (m == null) throw new NoSuchMethodException(instance.getClass().getName() + "." + name);
            return invokeMethod(m, args);
        }

        private Method findMethod(String name, Class<?>... types) {
            try {
                return instance.getClass().getMethod(name, types);
            } catch (NoSuchMethodException ignored) {
                for (Class<?> c = instance.getClass(); c != null; c = c.getSuperclass()) {
                    try {
                        Method m = c.getDeclaredMethod(name, types);
                        m.setAccessible(true);
                        return m;
                    } catch (NoSuchMethodException ignoredAgain) { }
                }
                return null;
            }
        }

        private Object invokeMethod(Method m, Object... args) throws Exception {
            try {
                m.setAccessible(true);
                return m.invoke(instance, args);
            } catch (InvocationTargetException ex) {
                Throwable cause = ex.getTargetException();
                if (cause instanceof Exception e) throw e;
                if (cause instanceof Error err) throw err;
                throw new RuntimeException(cause);
            }
        }

        @FunctionalInterface
        private interface ThrowingSupplier<T> {
            T get() throws Exception;
        }
    }
}
