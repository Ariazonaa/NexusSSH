// Terminal-Seite im WebView2: eine xterm.js-Instanz je Sitzung. Sichtbar ist das Terminal der aktiven Sitzung –
// oder mehrere, wenn die App die Fläche teilt: als Baum aus Zweigen neben- und untereinander, mit einer
// verschiebbaren Trennlinie zwischen je zwei Teilen.
// Die Nachrichten sind in NexusSSH.Core/Terminals/TerminalMessages.cs beschrieben – beide Seiten gemeinsam ändern.
(() => {
    'use strict';

    const root = document.getElementById('terminals');

    // Die Brücke zur App. Nur diese beiden Funktionen wissen, dass der Wirt ein WebView2 ist – ein anderer
    // eingebetteter Browser (Linux) braucht hier nur seine eigene Art, Nachrichten zu schicken und zu empfangen.
    const webview = window.chrome && window.chrome.webview;
    if (!webview) {
        root.textContent = 'NexusSSH terminal: no host.';
        return;
    }

    const post = message => webview.postMessage(message);
    const listen = handler => webview.addEventListener('message', event => handler(event.data));

    // Mehr Fundstellen meldet die Seite nicht; die App nimmt auch nicht mehr an (TerminalMessages.MaxHits).
    const MAX_HITS = 500;
    const MAX_HIT_LENGTH = 400;

    // So viele Terminals teilen sich höchstens die Fläche, so tief ist der Baum höchstens, und weniger als diesen
    // Anteil bekommt kein Teil eines Zweigs (SplitTree.MaxPanes, MaxDepth, MinShare).
    const MAX_PANES = 16;
    const MAX_DEPTH = 16;
    const MIN_SHARE = 0.05;

    // Zum Nachbarn springen gibt es nur in einer Teilung – sonst gehören die Tasten dem Programm im Terminal.
    const NEIGHBOR_COMMANDS = new Set(['terminal.focusLeft', 'terminal.focusRight', 'terminal.focusUp', 'terminal.focusDown']);

    // Länger wird der gespeicherte Verlauf nicht (TerminalMessages.MaxDumpLength).
    const MAX_DUMP_LENGTH = 16 * 1024 * 1024;
    // Wie in TerminalMessages.MaxSnapshotLength: Ein größerer Inhalt wandert nicht mit.
    const MAX_SNAPSHOT_LENGTH = 32 * 1024 * 1024;

    // Solange die App nichts anderes schickt: das Standardschema „Black“ (Campbell auf reinem Schwarz).
    let theme = {
        background: '#000000',
        foreground: '#cccccc',
        cursor: '#ffffff',
        cursorAccent: '#000000',
        selectionBackground: '#5a5a5a',
    };
    let fontSize = 14;
    let fontFamily = '"Cascadia Mono", "Cascadia Code", Consolas, monospace';

    // Wie PuTTY: Markieren kopiert, Rechtsklick fügt ein (Einstellung der App).
    let mouseCopyPaste = false;

    // Aussehen des Cursors und der Zeilen (Einstellungen der App); die Grenzen prüft applyOptions noch einmal.
    let look = { cursorStyle: 'block', cursorBlink: true, lineHeight: 1, letterSpacing: 0, ligatures: true };

    // Einfügen bereinigen: was beim Kopieren aus Webseiten und Dokumenten mitkommt und in einer Shell stört.
    // Ein Bild hinter den Terminals (Einstellung der App, von Haus aus keines). Mit Bild ist der Hintergrund der
    // Terminals durchsichtig; die Farbe des Farbschemas liegt dann nur noch unter dem Bild.
    let backdrop = false;
    // Bilder, die der Server ins Terminal zeichnet (Sixel, das Protokoll von iTerm2) – nur, wenn der Benutzer es
    // eingeschaltet hat. Das Entschlüsseln fremder Bilddaten ist Angriffsfläche; deshalb aus, mit festen Grenzen.
    let images = false;
    let pasteClean = true;
    let pasteTabs = false;

    // Was über einer anklickbaren Adresse als Hinweis steht (übersetzt, von der App).
    let linkHint = '';

    // Regeln zum Hervorheben (Einstellung der App): {expression: RegExp, color: Name einer Farbe des Schemas}.
    let highlights = [];
    const HIGHLIGHT_LINES = 500;
    const HIGHLIGHT_MATCHES = 20;
    const HIGHLIGHT_DECORATIONS = 2000;

    // Gespeicherte Befehle, die beim Tippen vorgeschlagen werden (Einstellung der App): {name, text}.
    // „typed“ ist, was seit dem letzten Enter im Terminal „typedId“ an gewöhnlichen Zeichen getippt wurde – nur
    // zum Vergleichen, nur hier im Speicher der Seite; jede andere Taste leert es.
    let suggestions = [];
    let typed = '';
    let typedId = null;
    let offered = [];
    const SUGGESTIONS_SHOWN = 4;
    const SUGGESTION_MIN = 2;

    // Ergänzen über die gespeicherten Befehle hinaus (Einstellungen der App): Tabellen bekannter Programme
    // (Programm → Unterbefehle, Schalter, Schalter je Unterbefehl) und die Programme, hinter denen ein Name aus dem
    // Verzeichnis der Shell steht. Die Namen selbst schickt die App je Terminal ('names'). Verglichen wird hier,
    // mit „typed“ – die App erfährt nicht, was getippt wird; sie liefert nur Listen.
    let tables = new Map();
    let pathCommands = new Set();
    let dirCommands = new Set();
    const COMPLETIONS_SHOWN = 6;
    const NAMES_MAX = 2000;
    const CHILDREN_MAX = 200;
    const WORDS_MAX = 4000;

    // Hosts der gespeicherten Server (für ssh, scp …) und ob Tab auch eine Ergänzung annimmt – Einstellungen der App.
    let hosts = [];
    let tabCompletes = false;

    // Was jede Shell selbst kennt und in keinem Verzeichnis steht – zum ersten Wort dazu, sobald die Programme des
    // Servers bekannt sind.
    const BUILTINS = ['alias', 'bg', 'cd', 'echo', 'exit', 'export', 'fg', 'history', 'jobs', 'kill', 'pwd', 'read', 'source', 'type', 'ulimit', 'umask', 'unalias', 'unset', 'wait'];

    // Hinter diesen Unterbefehlen steht der Name eines Dienstes bzw. eines Containers, hinter diesen Programmen ein Host.
    const HOST_COMMANDS = new Set(['ssh', 'scp', 'sftp', 'rsync', 'ping', 'ssh-copy-id', 'mosh', 'telnet']);
    const CONTROL = /[\u0000-\u001f\u007f-\u009f‎‏‪-‮⁦-⁩]/;

    /** @type {{code: string, shift: boolean, id: string}[]} Tastenkürzel der App, die hier gemeldet werden. */
    let shortcuts = [];

    /** @type {Map<string, {term: Terminal, fit: any, search: any, frame: HTMLElement, surface: HTMLElement, opened: boolean, cols: number, rows: number}>} */
    const terminals = new Map();

    /** Die sichtbaren Terminals in der Reihenfolge von links oben nach rechts unten. */
    let panes = [];
    let activeId = null;
    let fitQueued = false;

    // Die Anordnung, wie die App sie zuletzt geschickt hat (geprüft, readTree): Blätter {id}, Zweige {dir, shares,
    // items, path, whole, element, dividers}. „shape“ fasst Aufbau und Vergrößerung als Text zusammen – ist er bei der
    // nächsten Nachricht gleich, werden nur die Anteile übernommen und nichts umgehängt. „top“ ist das oberste
    // Element der Anordnung in der Fläche (ein Rahmen oder ein Zweig).
    let tree = null;
    let shape = '';
    let zoomId = null;
    let fixedLayout = false;
    let top = null;

    // Was die Maus gerade zieht (eine Trennlinie oder eine Kopfzeile): {node, stop}. Ein neuer Aufbau bricht es ab.
    let gesture = null;

    // Ab so vielen Punkten Bewegung ist ein Druck auf eine Kopfzeile oder Trennlinie ein Ziehen, davor ein Klick.
    const DRAG_START = 6;

    // Solange an einer Trennlinie der Bereiche von tmux gezogen wird, behalten die Terminals ihre Größen für sich:
    // Jede Größe, die die App erfährt, geht als Befehl an tmux, und tmux zeichnet danach alle Bereiche neu. Beim
    // Loslassen melden sie die, die dann gilt (releaseSizes).
    let holdSizes = false;

    // Die Namen in den Kopfzeilen tragen eine Kennung: Eine Trennlinie heißt für eine Sprachausgabe wie der Teil vor ihr.
    let nameCount = 0;

    const suggestionList = document.createElement('div');
    suggestionList.className = 'suggestions';
    suggestionList.hidden = true;
    root.append(suggestionList);

    // Die Kennfarbe des Servers als Streifen am linken Rand des Terminals; nur eine schlichte Farbangabe.
    function mark(frame, color) {
        if (typeof color === 'string' && /^#[0-9a-fA-F]{6}$/.test(color)) {
            frame.style.setProperty('--mark', color);
        } else {
            frame.style.removeProperty('--mark');
        }
    }

    // Die Kopfzeile eines Terminals, zu sehen nur bei geteilter Fläche (terminal.css): ein Punkt für den Zustand,
    // der Name der Sitzung, ein × zum Herausnehmen. Der Name kann vom Server stammen – er wird als Text gesetzt.
    // An ihr lässt sich das Terminal an eine Kante eines anderen ziehen; ein Doppelklick schaltet es groß und zurück.
    const STATES = ['connected', 'connecting', 'closed'];

    function header(id) {
        const bar = document.createElement('div');
        bar.className = 'terminal-header';
        const state = document.createElement('span');
        state.className = 'state closed';
        // Steht nur da, solange das Terminal groß geschaltet ist (terminal.css).
        const zoom = document.createElement('span');
        zoom.className = 'zoom';
        zoom.textContent = '\u2922';
        zoom.setAttribute('aria-hidden', 'true');
        const name = document.createElement('span');
        name.className = 'name';
        name.id = `pane-name-${++nameCount}`;
        const leave = document.createElement('button');
        leave.type = 'button';
        leave.className = 'leave';
        leave.textContent = '\u00D7';
        leave.hidden = true;
        leave.tabIndex = -1;

        // Nicht erst dieses Terminal aktiv machen: Es soll gehen, die übrigen bleiben, wie sie sind.
        leave.addEventListener('pointerdown', event => event.stopPropagation());
        leave.addEventListener('click', event => {
            event.stopPropagation();
            post({ type: 'unsplit', id });
            terminals.get(activeId)?.term.focus();
        });
        leave.addEventListener('dblclick', event => event.stopPropagation());

        bar.addEventListener('pointerdown', event => dragHeader(event, id));
        // Die Bereiche eines Fensters von tmux ordnet tmux; groß schalten lässt die App sie nicht.
        bar.addEventListener('dblclick', () => {
            if (!fixedLayout && leavesOf(tree).length > 1) {
                post({ type: 'shortcut', key: 'terminal.zoomPane' });
            }
        });
        bar.append(state, zoom, name, leave);
        return { bar, state, name, leave };
    }

    function label(id, text, state, hint) {
        const entry = terminals.get(id);
        if (!entry) {
            return;
        }

        entry.head.name.textContent = typeof text === 'string' ? text.slice(0, 200) : '';
        entry.head.state.className = `state ${STATES.includes(state) ? state : 'closed'}`;
        const leave = typeof hint === 'string' && hint.length > 0;
        entry.head.leave.hidden = !leave;
        entry.head.leave.title = leave ? hint.slice(0, 200) : '';
        entry.head.leave.setAttribute('aria-label', leave ? hint.slice(0, 200) : '');
    }

    // Meldungen eines Programms (OSC 9, OSC 777): { title, body } oder null, wenn es keine gibt. Titel höchstens 80,
    // Text höchstens 240 Zeichen, ohne Steuerzeichen und ohne Zeichen, die die Schreibrichtung drehen – die App
    // prüft es noch einmal (TerminalMessages.NoticeText).
    //  OSC 9: ganz der Text (iTerm2). `9;4;…` ist bei Windows Terminal und ConEmu ein Fortschritt – wie die
    //  übrigen Unterbefehle von ConEmu (`9;1` bis `9;12`, etwa `9;9` = Arbeitsverzeichnis, das Programme wie
    //  oh-my-posh bei jeder Eingabeaufforderung schicken) keine Meldung.
    //  OSC 777: nur `notify;Titel;Text` – der Titel reicht bis zum nächsten `;`, der Rest ist der Text, auch mit `;`.
    function noticeOf(code, data) {
        let title = '';
        let body = '';
        if (code === 9) {
            if (/^(?:[1-9]|1[0-2])(?:;|$)/.test(data)) {
                return null;
            }

            body = data;
        } else if (code === 777 && data.startsWith('notify;')) {
            const rest = data.slice('notify;'.length);
            const cut = rest.indexOf(';');
            title = cut < 0 ? rest : rest.slice(0, cut);
            body = cut < 0 ? '' : rest.slice(cut + 1);
        } else {
            return null;
        }

        const tidy = (text, limit) => {
            const clean = text.slice(0, limit * 4)
                .replace(/[\p{Cc}\u{2028}\u{2029}\u{200e}\u{200f}\u{202a}-\u{202e}\u{2066}-\u{2069}]/gu, '')
                .trim();
            const units = Array.from(clean);
            return units.length <= limit ? clean : units.slice(0, limit - 1).join('') + '…';
        };

        title = tidy(title, 80);
        body = tidy(body, 240);
        return title || body ? { title, body } : null;
    }

    function open(id, color, own) {
        if (terminals.has(id)) {
            mark(terminals.get(id).frame, color);
            setTheme(id, own);
            return;
        }

        const frame = document.createElement('div');
        frame.className = 'terminal-frame';
        frame.hidden = true;
        mark(frame, color);
        const head = header(id);
        const surface = document.createElement('div');
        surface.className = 'terminal-surface';
        frame.append(head.bar, surface);
        root.append(frame);

        const term = new Terminal({
            theme: shownTheme(ownTheme(own)),
            // Muss vor dem Öffnen feststehen und lässt sich danach nicht mehr ändern – gebraucht wird es nur mit
            // einem Bild dahinter.
            allowTransparency: true,
            fontFamily,
            fontSize,
            cursorBlink: look.cursorBlink,
            cursorStyle: look.cursorStyle,
            lineHeight: look.lineHeight,
            letterSpacing: look.letterSpacing,
            scrollback: 10000,
            // Die Hervorhebungen der Suche sind „Decorations“.
            allowProposedApi: true,
            // Links, die der Server selbst auszeichnet (OSC 8): Ohne eigenen Umgang zeigte xterm bei jedem
            // schlichten Klick eine Rückfrage mit dem Text des Servers und hielte dabei alle Terminals an. Sie
            // gehen denselben Weg wie erkannte Adressen – nur mit Strg, und was daraus wird, prüft die App.
            linkHandler: {
                activate(event, uri) {
                    if (event.ctrlKey && typeof uri === 'string') {
                        post({ type: 'link', id, data: uri });
                    }
                },
            },
        });
        const fit = new FitAddon.FitAddon();
        const search = new SearchAddon.SearchAddon();
        term.loadAddon(fit);
        term.loadAddon(search);

        // names: die Namen im Verzeichnis der Shell (von der App); cwdSeen: die Shell meldet ihr Verzeichnis selbst
        // (OSC 7); moved: sie hat es gewechselt, ohne es zu melden – dann weiß niemand mehr, wo sie steht.
        const entry = { term, fit, search, frame, surface, head, opened: false, cols: 0, rows: 0, mark: null, fresh: [], colored: [], image: null, theme: ownTheme(own), names: [], cwdSeen: false, moved: false, commands: [] };
        terminals.set(id, entry);
        paint(entry);
        applyImages(entry);

        term.onData(data => {
            post({ type: 'input', id, data });
            noteTyped(id, entry, data);
        });
        term.onResize(() => reportSize(id, entry));
        term.onWriteParsed(() => {
            highlight(entry);

            // Das Echo des Getippten ist da: Erst jetzt lässt sich sagen, ob die Zeile zeigt, was getippt wurde.
            if (typedId === id && typed) {
                offer(entry);
            }
        });
        term.attachCustomKeyEventHandler(event => handleKey(term, event));
        search.onDidChangeResults(result => post({ type: 'found', id, index: result.resultIndex, count: result.resultCount }));

        // Die Glocke (BEL) meldet die App als Hinweis, wenn ihr Fenster im Hintergrund ist.
        term.onBell(() => post({ type: 'bell', id }));

        // OSC 0/2: Ein Programm setzt den Titel des Terminals. Ob er am Reiter steht, entscheidet die App.
        term.onTitleChange(title => post({ type: 'title', id, data: String(title).slice(0, 1024) }));

        // OSC 7: Die Shell nennt ihr Arbeitsverzeichnis als file://host/pfad – der Dateibrowser kann ihm folgen.
        // Nur der Pfad geht an die App, und die prüft ihn noch einmal (er kommt vom Server).
        term.parser.registerOscHandler(7, data => {
            const match = /^file:\/\/[^/]*(\/.*)$/.exec(data);
            if (match) {
                entry.cwdSeen = true;
                try {
                    post({ type: 'cwd', id, data: decodeURIComponent(match[1]) });
                } catch {
                    // Kein gültig kodierter Pfad: dann eben keine Meldung.
                }
            }

            return true;
        });

        // OSC 9 (iTerm2: Text) und OSC 777 (urxvt, foot: notify;Titel;Text): Ein Programm meldet etwas, etwa
        // „Build fertig“. Die Seite meldet nur Titel und Text, gesäubert und gekürzt – ob und wie sie erscheinen,
        // entscheidet die App, die beides noch einmal prüft (der Text kommt vom Server).
        for (const code of [9, 777]) {
            term.parser.registerOscHandler(code, data => {
                const notice = noticeOf(code, data);
                if (notice) {
                    post({ type: 'notify', id, title: notice.title, body: notice.body });
                }

                return true;
            });
        }

        // Web-Adressen und absolute Pfade in der Ausgabe lassen sich mit Strg+Klick öffnen. Die Seite meldet nur,
        // was angeklickt wurde – ob und wie es geöffnet wird, entscheidet die App, die es noch einmal prüft.
        term.registerLinkProvider({
            provideLinks(row, callback) {
                // Eine Zeile der Ausgabe kann über mehrere Zeilen des Terminals laufen: Was am Rand umbricht,
                // gehört zusammen. Gesucht wird deshalb in der ganzen umbrochenen Zeile, und gemeldet, was die
                // gefragte Zeile berührt – mit einem Bereich, der über den Umbruch reicht.
                const buffer = term.buffer.active;
                let first = row - 1;
                while (first > 0 && row - 1 - first < MAX_WRAPPED_ROWS && buffer.getLine(first)?.isWrapped) {
                    first--;
                }

                let last = row - 1;
                while (last + 1 < buffer.length && last + 1 - first < MAX_WRAPPED_ROWS && buffer.getLine(last + 1)?.isWrapped) {
                    last++;
                }

                let joined = '';
                const cells = [];
                for (let y = first; y <= last; y++) {
                    const line = buffer.getLine(y);
                    if (!line) {
                        break;
                    }

                    const part = cellsOf(line, term.cols);
                    joined += part.text;
                    for (const x of part.columns) {
                        cells.push({ x: x + 1, y: y + 1 });
                    }
                }

                const links = linksIn(joined).filter(found => cells[found.first].y <= row && cells[found.last].y >= row);
                callback(links.length > 0 ? links.map(found => ({
                    range: { start: cells[found.first], end: cells[found.last] },
                    text: found.text,
                    decorations: { pointerCursor: true, underline: true },
                    activate(event, text) {
                        if (event.ctrlKey) {
                            post({ type: found.type, id, data: text });
                        }
                    },
                    hover() {
                        frame.title = linkHint;
                    },
                    leave() {
                        frame.title = '';
                    },
                })) : undefined);
            },
        });

        // Wie PuTTY, wenn eingestellt: Was markiert wird, liegt gleich in der Zwischenablage, und ein Rechtsklick
        // fügt ein. Lesen darf die Seite die Zwischenablage nicht – sie bittet die App darum.
        term.onSelectionChange(() => {
            if (mouseCopyPaste && term.hasSelection()) {
                navigator.clipboard.writeText(term.getSelection()).catch(() => { });
            }
        });
        // Strg+V löst im Browser ein paste-Ereignis aus. Es wird hier abgefangen, bevor xterm es sieht: Der Text
        // geht erst durch cleanPaste und dann über xterm ins Terminal (das klammert ihn ein, wenn die Shell es will).
        frame.addEventListener('paste', event => {
            const text = event.clipboardData ? event.clipboardData.getData('text/plain') : '';
            event.preventDefault();
            event.stopPropagation();
            pasteInto(term, text, id);
        }, true);
        frame.addEventListener('contextmenu', event => {
            if (mouseCopyPaste) {
                event.preventDefault();
                post({ type: 'paste', id });
            }
        });

        // Ein Klick in das andere Terminal der geteilten Ansicht macht dessen Sitzung in der App aktiv – ebenso der
        // Fokus, wenn er mit der Tastatur hineinkommt (Tab von einer Trennlinie aus).
        const activate = () => {
            if (activeId !== id && panes.includes(id)) {
                setActive(id);
                post({ type: 'focus', id });
            }
        };
        frame.addEventListener('pointerdown', () => {
            forgetTyped();
            activate();
        });
        frame.addEventListener('focusin', () => {
            if (activeId !== id) {
                forgetTyped();
                activate();
            }
        });
    }

    // --- Anordnung ---

    // Der Baum aus der Nachricht der App, geprüft: nur Terminals, die die Seite kennt, jedes einmal, höchstens
    // MAX_PANES Blätter und MAX_DEPTH Zweige tief; Anteile endlich und positiv, sonst gleich verteilt. Fällt ein Teil
    // heraus, rückt der Rest zusammen. „path“ sind die Nummern der Kinder ab der Wurzel, wie die App sie kennt – so
    // meldet die Seite geänderte Anteile. Hat ein Zweig nicht mehr alle Kinder der Nachricht („whole“ falsch),
    // passen die Nummern nicht mehr, und er meldet nichts.
    function readTree(node, path, seen) {
        if (!node || typeof node !== 'object' || path.length > MAX_DEPTH || ++seen.nodes > 4 * MAX_PANES) {
            return null;
        }

        if (typeof node.id === 'string') {
            if (!terminals.has(node.id) || seen.ids.has(node.id) || seen.ids.size >= MAX_PANES) {
                return null;
            }

            seen.ids.add(node.id);
            return { id: node.id };
        }

        if ((node.dir !== 'row' && node.dir !== 'column') || path.length >= MAX_DEPTH || !Array.isArray(node.items)
            || node.items.length < 2 || node.items.length > MAX_PANES) {
            return null;
        }

        const given = Array.isArray(node.shares) && node.shares.length === node.items.length
            && node.shares.every(share => Number.isFinite(share) && share > 0);
        const items = [];
        const weights = [];
        node.items.forEach((item, index) => {
            const child = readTree(item, [...path, index], seen);
            if (child) {
                items.push(child);
                weights.push(given ? node.shares[index] : 1);
            }
        });

        if (items.length < 2) {
            return items[0] ?? null;
        }

        const sum = weights.reduce((total, weight) => total + weight, 0);
        return {
            dir: node.dir,
            items,
            shares: weights.map(weight => weight / sum),
            path,
            whole: given && items.length === node.items.length,
            element: null,
            dividers: [],
        };
    }

    function shapeOf(node) {
        return node.dir ? `${node.dir}(${node.items.map(shapeOf).join(',')})` : JSON.stringify(node.id);
    }

    function leavesOf(node) {
        return !node ? [] : node.dir ? node.items.flatMap(leavesOf) : [node.id];
    }

    // Das Element eines Knotens: der Rahmen eines Terminals oder der Container eines Zweigs.
    function elementOf(node) {
        return node.dir ? node.element : terminals.get(node.id)?.frame;
    }

    function idOf(frame) {
        for (const [id, entry] of terminals) {
            if (entry.frame === frame) {
                return id;
            }
        }

        return null;
    }

    // Zeigt die Anordnung, die die App schickt, und gibt dem aktiven Terminal den Fokus. Ist der Aufbau derselbe wie
    // zuletzt (so antwortet die App auf gemeldete Anteile), werden nur die Anteile übernommen – nichts wird
    // umgehängt, und ein Zweig, an dessen Trennlinie gerade gezogen wird, behält, was der Benutzer zieht. Sonst
    // entstehen die Zweige neu; die Rahmen der Terminals werden dabei nur umgehängt, nie neu gebaut (der Verlauf
    // bleibt). „zoom“: dieses Terminal bekommt die ganze Fläche; „fixed“: die Anordnung gehört tmux.
    function layout(given, active, zoom, fixed) {
        forgetTyped();
        const next = readTree(given, [], { ids: new Set(), nodes: 0 });
        const leaves = leavesOf(next);
        const zoomed = leaves.length > 1 && leaves.includes(zoom) ? zoom : null;
        const nextShape = next ? `${zoomed ?? ''}|${shapeOf(next)}` : '';
        fixedLayout = fixed === true;
        if (tree && next && nextShape === shape) {
            takeShares(tree, next);
        } else {
            build(next, zoomed);
            shape = nextShape;
        }

        // Die Kopfzeilen gibt es, sobald die Fläche geteilt ist – auch groß geschaltet: Ein Doppelklick dort holt
        // die übrigen zurück.
        root.classList.toggle('split', leaves.length > 1);
        root.classList.toggle('zoomed', zoomed !== null);
        root.classList.toggle('fixed', fixedLayout);

        for (const id of panes) {
            const entry = terminals.get(id);

            // Erst öffnen, wenn das Element sichtbar ist: xterm misst beim Öffnen die Zeichengröße, und ein
            // verstecktes Element hat keine.
            if (!entry.opened) {
                entry.term.open(entry.surface);
                entry.opened = true;
            }

            entry.fit.fit();
            reportSize(id, entry);
        }

        setActive(panes.includes(active) ? active : panes[0] ?? null);

        // Wer gerade eine Trennlinie mit den Pfeiltasten schiebt, behält dort den Fokus.
        if (!document.activeElement?.classList.contains('divider')) {
            terminals.get(activeId)?.term.focus();
        }
    }

    // Dieselbe Anordnung noch einmal: Anteile und Nummern aus der neuen übernehmen – außer im Zweig, an dessen
    // Trennlinie gerade gezogen wird.
    function takeShares(current, next) {
        if (!current.dir) {
            return;
        }

        current.path = next.path;
        current.whole = next.whole;
        if (gesture?.node !== current && current.shares.some((share, index) => Math.abs(share - next.shares[index]) > 1e-9)) {
            current.shares = next.shares;
            applyShares(current);
        }

        current.items.forEach((item, index) => takeShares(item, next.items[index]));
    }

    // Baut die Anordnung neu auf: die Zweige als Container, die Rahmen der Terminals hineingehängt. Was nicht mehr
    // zu sehen ist, geht versteckt in die Fläche zurück; die alten Zweige fallen weg.
    function build(next, zoomed) {
        endGesture();
        const old = top;
        tree = next;
        zoomId = zoomed;
        panes = zoomed ? [zoomed] : leavesOf(next);
        top = !next ? null : zoomed ? terminals.get(zoomed).frame : render(next);
        if (top && !top.classList.contains('split')) {
            top.hidden = false;
            top.style.flex = '';
        }

        if (top && top.parentElement !== root) {
            root.prepend(top);
        }

        for (const [id, entry] of terminals) {
            if (!panes.includes(id)) {
                entry.frame.hidden = true;
                entry.frame.style.flex = '';
                if (entry.frame.parentElement !== root) {
                    root.append(entry.frame);
                }
            }
        }

        if (old && old !== top && old.classList.contains('split')) {
            old.remove();
        }
    }

    // Ein Blatt ist der Rahmen seines Terminals, ein Zweig ein Flex-Container mit einer Trennlinie zwischen je zwei Teilen.
    function render(node) {
        if (!node.dir) {
            const frame = terminals.get(node.id).frame;
            frame.hidden = false;
            return frame;
        }

        const element = document.createElement('div');
        element.className = `split ${node.dir}`;
        node.element = element;
        node.dividers = [];
        node.items.forEach((item, index) => {
            if (index > 0) {
                const line = divider(node, index - 1);
                node.dividers.push(line);
                element.append(line);
            }

            element.append(render(item));
        });
        applyShares(node);
        return element;
    }

    // Die Anteile eines Zweigs als Flex-Gewichte seiner Teile (die Trennlinien haben ihre feste Breite) und als Stand
    // der Trennlinien für eine Sprachausgabe (in Prozent des Zweigs).
    function applyShares(node) {
        if (!node.element) {
            return;
        }

        let position = 0;
        node.items.forEach((item, index) => {
            elementOf(item).style.flex = `${node.shares[index] * 100} 1 0px`;
            position += node.shares[index];
            node.dividers[index]?.setAttribute('aria-valuenow', String(Math.round(position * 100)));
        });
    }

    // Die Trennlinie zwischen den Teilen „index“ und „index + 1“ eines Zweigs: mit der Maus ziehen; angeklickt (ohne
    // Ziehen) hat sie den Fokus, dann schieben sie die Pfeiltasten. Ein Doppelklick gibt allen Teilen des Zweigs gleich
    // viel Platz.
    function divider(node, index) {
        const line = document.createElement('div');
        line.className = 'divider';
        line.tabIndex = 0;
        line.setAttribute('role', 'separator');
        line.setAttribute('aria-orientation', node.dir === 'row' ? 'vertical' : 'horizontal');
        line.setAttribute('aria-valuemin', '0');
        line.setAttribute('aria-valuemax', '100');
        const first = terminals.get(leavesOf(node.items[index])[0]);
        if (first) {
            line.setAttribute('aria-labelledby', first.head.name.id);
        }

        line.addEventListener('pointerdown', event => dragDivider(event, node, index, line));
        line.addEventListener('keydown', event => stepDivider(event, node, index));
        line.addEventListener('dblclick', () => {
            node.shares = node.items.map(() => 1 / node.items.length);
            applyShares(node);
            queueFit();
            report(node);
        });
        return line;
    }

    // Trennlinie ziehen: verschiebt die Grenze zwischen den beiden Teilen daneben, die übrigen bleiben. Unterwegs
    // passen sich nur die Terminals an; erst beim Loslassen erfährt die App die Anteile. Wer gezogen hat, tippt danach
    // im Terminal weiter – wie nach dem Ziehen eines Kopfes. Ein Klick ohne Ziehen wählt die Linie: Sie behält den
    // Fokus, und die Pfeiltasten schieben sie (aus dem Terminal führt sonst keine Taste zu ihr – Tab gehört der Shell).
    function dragDivider(event, node, index, line) {
        if (event.button !== 0) {
            return;
        }

        // Ohne das holte sich der Browser beim Drücken den Fokus selbst – und die Auswahl von Text begänne.
        event.preventDefault();
        const row = node.dir === 'row';
        const before = elementOf(node.items[index]).getBoundingClientRect();
        const after = elementOf(node.items[index + 1]).getBoundingClientRect();
        const start = row ? before.left : before.top;
        const gap = row ? line.offsetWidth : line.offsetHeight;
        const room = (row ? after.right : after.bottom) - start - gap;
        const pair = node.shares[index] + node.shares[index + 1];
        if (!(room > 0)) {
            return;
        }

        const from = [...node.shares];
        const pressed = row ? event.clientX : event.clientY;
        let moving = false;
        line.classList.add('dragging');
        holdSizes = fixedLayout;
        begin(event, line, node, moveEvent => {
            const at = row ? moveEvent.clientX : moveEvent.clientY;
            if (!moving && Math.abs(at - pressed) < DRAG_START) {
                return;
            }

            moving = true;
            resize(node, index, pair * (at - start - (gap / 2)) / room);
        }, () => {
            line.classList.remove('dragging');
            if (node.shares.some((share, other) => share !== from[other])) {
                report(node);
            }

            releaseSizes();
            if (moving) {
                terminals.get(activeId)?.term.focus();
            } else {
                line.focus();
            }
        }, () => {
            line.classList.remove('dragging');
            node.shares = from;
            applyShares(node);
            queueFit();
            releaseSizes();
        });
    }

    // Der Befehl der App „Trennlinie verschieben“: die Linie hinter dem Terminal, beim letzten Teil eines Zweigs die
    // davor; steht es in seinem Zweig an keiner, die des Zweigs darüber.
    function focusDivider(id) {
        const entry = terminals.get(id);
        if (!entry || zoomId || !panes.includes(id) || panes.length < 2) {
            return;
        }

        const isLine = element => element?.classList.contains('divider') ? element : null;
        for (let element = entry.frame; element && element !== root; element = element.parentElement) {
            const line = isLine(element.nextElementSibling) ?? isLine(element.previousElementSibling);
            if (line) {
                line.focus();
                return;
            }
        }
    }

    // Nach dem Ziehen: Jedes Terminal meldet die Größe, die jetzt gilt (die Meldung, die unterwegs fällig war, kam
    // nicht hinaus). Passt das Terminal sich danach noch an (queueFit), meldet es das wie immer.
    function releaseSizes() {
        if (!holdSizes) {
            return;
        }

        holdSizes = false;
        for (const id of panes) {
            const entry = terminals.get(id);
            if (entry?.opened) {
                reportSize(id, entry);
            }
        }
    }

    // Mit den Pfeiltasten: ein Schritt sind zwei Hundertstel des Zweigs, mit Umschalt zehn. Esc geht ins Terminal zurück.
    function stepDivider(event, node, index) {
        if (event.ctrlKey || event.altKey || event.metaKey) {
            return;
        }

        if (event.key === 'Escape') {
            event.preventDefault();
            terminals.get(activeId)?.term.focus();
            return;
        }

        const direction = event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1
            : event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1 : 0;
        if (direction === 0) {
            return;
        }

        event.preventDefault();
        const before = node.shares[index];
        resize(node, index, before + (direction * (event.shiftKey ? 0.1 : 0.02)));
        if (node.shares[index] !== before) {
            report(node);
        }
    }

    // Setzt die Grenze zwischen zwei Teilen eines Zweigs; jeder behält mindestens MIN_SHARE.
    function resize(node, index, first) {
        const pair = node.shares[index] + node.shares[index + 1];
        if (pair < 2 * MIN_SHARE || !Number.isFinite(first)) {
            return;
        }

        node.shares[index] = Math.min(pair - MIN_SHARE, Math.max(MIN_SHARE, first));
        node.shares[index + 1] = pair - node.shares[index];
        applyShares(node);
        queueFit();
    }

    // Meldet der App die Anteile eines Zweigs; sie übernimmt sie in ihren Baum und schickt die Anordnung zurück.
    // Gerundet: Die App lehnt Anteile unter MIN_SHARE ab, und ein Rest aus der Rechnung (0,0499999…) sähe so aus.
    function report(node) {
        if (node.whole) {
            post({ type: 'shares', id: leavesOf(node)[0], path: node.path, shares: node.shares.map(share => Math.round(share * 1e6) / 1e6) });
        }
    }

    // Kopfzeile ziehen: Das Terminal lässt sich an eine Kante eines anderen derselben Teilung stellen – die App
    // stellt es dorthin um und schickt die neue Anordnung. Erst ab ein paar Punkten Bewegung; davor bleibt es ein
    // Klick (oder Doppelklick). Nicht bei den Bereichen eines Fensters von tmux (die ordnet tmux) und nicht, solange
    // ein Terminal groß geschaltet ist.
    function dragHeader(event, id) {
        if (event.button !== 0 || fixedLayout || zoomId || panes.length < 2 || !panes.includes(id)) {
            return;
        }

        const source = terminals.get(id).frame;
        const startX = event.clientX;
        const startY = event.clientY;
        let moving = false;
        let target = null;
        const show = next => {
            if (target && (!next || target.frame !== next.frame || target.side !== next.side)) {
                target.frame.classList.remove(`drop-${target.side}`);
            }

            if (next) {
                next.frame.classList.add(`drop-${next.side}`);
            }

            target = next;
        };
        const end = () => {
            show(null);
            source.classList.remove('moving');
            root.classList.remove('moving');
        };

        begin(event, terminals.get(id).head.bar, null, moveEvent => {
            if (!moving && Math.hypot(moveEvent.clientX - startX, moveEvent.clientY - startY) < DRAG_START) {
                return;
            }

            moving = true;
            source.classList.add('moving');
            root.classList.add('moving');
            show(dropTarget(moveEvent.clientX, moveEvent.clientY, id));
        }, () => {
            const chosen = target;
            end();
            if (moving && chosen) {
                post({ type: 'move', id, target: chosen.id, side: chosen.side });
            }
        }, end);
    }

    // Das Terminal unter dem Zeiger (nicht das gezogene) und die Kante, der der Zeiger darin am nächsten ist.
    function dropTarget(x, y, source) {
        const frame = document.elementFromPoint(x, y)?.closest('.terminal-frame');
        const id = frame ? idOf(frame) : null;
        if (!id || id === source || !panes.includes(id)) {
            return null;
        }

        const box = frame.getBoundingClientRect();
        const across = (x - box.left) / box.width;
        const down = (y - box.top) / box.height;
        const [side] = [['left', across], ['right', 1 - across], ['top', down], ['bottom', 1 - down]]
            .reduce((best, edge) => edge[1] < best[1] ? edge : best);
        return { id, frame, side };
    }

    // Ein Ziehen mit der Maus: Der Zeiger gehört bis zum Loslassen dem Element, wenn der Browser es zulässt; die
    // Bewegungen kommen über das Fenster, also auch ohne Fang. Esc bricht ab, ebenso ein neuer Aufbau der Anordnung.
    function begin(event, element, node, move, finish, cancel) {
        endGesture();
        const pointer = event.pointerId;
        let captured = false;
        try {
            element.setPointerCapture(pointer);
            captured = true;
        } catch {
            // Ohne Fang folgt das Ziehen dem Zeiger nur, solange er über der Seite bleibt – besser als gar nicht.
        }

        const onMove = moveEvent => {
            if (moveEvent.pointerId === pointer) {
                move(moveEvent);
            }
        };
        const onUp = upEvent => {
            if (upEvent.pointerId === pointer) {
                stop();
                finish();
            }
        };
        const onCancel = cancelEvent => {
            if (cancelEvent.pointerId === pointer) {
                stop();
                cancel();
            }
        };
        const onKey = keyEvent => {
            if (keyEvent.key === 'Escape') {
                keyEvent.preventDefault();
                keyEvent.stopPropagation();
                stop();
                cancel();
            }
        };
        const stop = () => {
            window.removeEventListener('pointermove', onMove, true);
            window.removeEventListener('pointerup', onUp, true);
            window.removeEventListener('pointercancel', onCancel, true);
            window.removeEventListener('keydown', onKey, true);
            element.removeEventListener('lostpointercapture', onCancel);
            if (captured && element.hasPointerCapture(pointer)) {
                element.releasePointerCapture(pointer);
            }

            gesture = null;
        };

        window.addEventListener('pointermove', onMove, true);
        window.addEventListener('pointerup', onUp, true);
        window.addEventListener('pointercancel', onCancel, true);
        window.addEventListener('keydown', onKey, true);
        if (captured) {
            element.addEventListener('lostpointercapture', onCancel);
        }

        gesture = {
            node,
            stop() {
                stop();
                cancel();
            },
        };
    }

    function endGesture() {
        gesture?.stop();
    }

    function setActive(id) {
        activeId = id;
        for (const [otherId, other] of terminals) {
            other.frame.classList.toggle('active', otherId === id);
        }
    }

    function close(id) {
        const entry = terminals.get(id);
        if (!entry) {
            return;
        }

        endGesture();
        entry.term.dispose();
        forgetTyped();
        entry.frame.remove();
        terminals.delete(id);
        panes = panes.filter(pane => pane !== id);
        if (activeId === id) {
            activeId = null;
        }
    }

    // Flusskontrolle: Ist ein Block verarbeitet, erfährt es die App. Stehen zu viele Bestätigungen aus, hält sie
    // die Sitzung an (TerminalOutputPump) – sonst liefe bei „cat große-datei“ der Speicher voll.
    function write(id, base64) {
        const entry = terminals.get(id);
        if (entry) {
            const bytes = Uint8Array.from(atob(base64), character => character.charCodeAt(0));
            entry.term.write(bytes, () => post({ type: 'ack', id, count: bytes.length }));
        }
    }

    // Der Code des Bild-Addons wird erst geholt, wenn der Benutzer Bilder einschaltet – sonst läuft er gar nicht.
    // Er liegt bei der Seite (vendor/), nicht im Netz.
    let imageAddonRequested = false;
    function loadImageAddon(then) {
        if (!images || typeof ImageAddon !== 'undefined') {
            then();
            return;
        }

        if (imageAddonRequested) {
            return;
        }

        imageAddonRequested = true;
        const script = document.createElement('script');
        script.src = 'vendor/addon-image.js';
        script.onload = then;
        document.head.append(script);
    }

    // Lädt oder entlädt das Bild-Addon eines Terminals nach der Einstellung. Die Grenzen sind fest: so viele Punkte
    // je Bild, so viel Speicher für alle Bilder eines Terminals, so große Daten je Bild – was darüber liegt, wird
    // verworfen, statt den Speicher der App zu füllen.
    function applyImages(entry) {
        if (images && !entry.image && typeof ImageAddon !== 'undefined') {
            try {
                entry.image = new ImageAddon.ImageAddon({
                    enableSizeReports: true,
                    pixelLimit: 8 * 1024 * 1024,
                    storageLimit: 64,
                    sixelSupport: true,
                    sixelSizeLimit: 12 * 1024 * 1024,
                    iipSupport: true,
                    iipSizeLimit: 12 * 1024 * 1024,
                    showPlaceholder: true,
                });
                entry.term.loadAddon(entry.image);
            } catch (error) {
                entry.image = null;
            }
        } else if (!images && entry.image) {
            entry.image.dispose();
            entry.image = null;
        }
    }

    // Das Farbschema, wie ein Terminal es bekommt: sein eigenes, sonst das allgemeine – mit einem Bild dahinter
    // ohne eigenen Hintergrund.
    function shownTheme(own) {
        const base = own || theme;
        return backdrop && base ? { ...base, background: 'rgba(0, 0, 0, 0)' } : base;
    }

    // Ein eigenes Farbschema für ein einzelnes Terminal (Farbschema des Profils). Angenommen wird nur, was ganz aus
    // Farben der Form #rrggbb besteht; alles andere heißt „keines“.
    const THEME_KEYS = ['background', 'foreground', 'cursor', 'cursorAccent', 'selectionBackground', 'black', 'red', 'green',
        'yellow', 'blue', 'magenta', 'cyan', 'white', 'brightBlack', 'brightRed', 'brightGreen', 'brightYellow',
        'brightBlue', 'brightMagenta', 'brightCyan', 'brightWhite'];

    function ownTheme(value) {
        if (!value || typeof value !== 'object') {
            return null;
        }

        const clean = {};
        for (const key of THEME_KEYS) {
            if (typeof value[key] !== 'string' || !/^#[0-9a-f]{6}$/i.test(value[key])) {
                return null;
            }

            clean[key] = value[key];
        }

        return clean;
    }

    // Farben eines Terminals setzen. Mit eigenem Schema füllt sein Rahmen die Fläche selbst – sonst scheint der
    // Hintergrund der Seite durch, der dem allgemeinen Schema folgt.
    function paint(entry) {
        entry.term.options.theme = shownTheme(entry.theme);
        entry.frame.style.background = entry.theme && !backdrop ? entry.theme.background : '';

        // Die Kopfzeile steht dann auf dem Hintergrund des eigenen Schemas – also auch in dessen Schriftfarbe.
        if (entry.theme) {
            entry.frame.style.setProperty('--foreground', entry.theme.foreground);
        } else {
            entry.frame.style.removeProperty('--foreground');
        }
    }

    function setTheme(id, value) {
        const entry = terminals.get(id);
        if (entry) {
            entry.theme = ownTheme(value);
            paint(entry);
        }
    }

    // Das Bild kommt von der App als data:-Adresse (die Seite lädt nichts von der Platte und nichts aus dem Netz).
    // Angenommen wird nur genau diese Form; alles andere räumt das Bild ab. Die Abdunklung liegt als Schleier
    // darüber, damit der Text lesbar bleibt.
    function applyBackdrop(message) {
        const image = typeof message.data === 'string' && /^data:image\/(png|jpeg|webp|bmp);base64,[A-Za-z0-9+\/]+=*$/.test(message.data) ? message.data : '';
        const dim = Number.isInteger(message.dim) ? Math.min(95, Math.max(0, message.dim)) / 100 : 0.7;
        backdrop = image.length > 0;
        root.classList.toggle('backdrop', backdrop);
        root.style.backgroundImage = backdrop ? `linear-gradient(rgba(0, 0, 0, ${dim}), rgba(0, 0, 0, ${dim})), url("${image}")` : '';
        for (const entry of terminals.values()) {
            paint(entry);
        }
    }

    // Was eingefügt wird, kommt oft aus einer Webseite oder einem Dokument – mit Zeichen, die man nicht sieht und
    // die in einer Shell stören oder schaden: Steuerzeichen (ESC könnte das „eingeklammerte Einfügen“ vorzeitig
    // beenden und den Rest wie getippt aussehen lassen), geschützte Leerzeichen, Zeichen ohne Breite. Der
    // Zeilenumbruch am Ende fällt weg, damit eine eingefügte Zeile nicht von selbst losläuft – Enter drückt der
    // Benutzer. Zeilenumbrüche dazwischen bleiben; Tabulatoren auf Wunsch als Leerzeichen (sonst lösen sie in der
    // Shell die Vervollständigung aus).
    function cleanPaste(text) {
        // Steuerzeichen fallen immer weg, auch wenn das Bereinigen abgeschaltet ist: Das ist kein Aufr\u00e4umen,
        // sondern der Schutz davor, dass eingef\u00fcgter Text das eingeklammerte Einf\u00fcgen beendet (ESC [ 201 ~) und
        // der Rest wie getippt l\u00e4uft. Abschalten l\u00e4sst sich nur das \u00dcbrige.
        let clean = text.replace(/\r\n?/g, '\n')
            .replace(/[\u0000-\u0008\u000b-\u001f\u007f-\u009f]/g, '');
        if (!pasteClean) {
            return clean;
        }

        clean = clean
            .replace(/[\u00a0\u202f\u2007]/g, ' ')
            .replace(/[\u200b-\u200d\u2060\ufeff]/g, '')
            .replace(/\n+$/, '');
        if (pasteTabs) {
            clean = clean.replace(/\t/g, '    ');
        }

        return clean;
    }

    // Mehrere Zeilen in eine Shell, die das eingeklammerte Einfügen nicht eingeschaltet hat (oder der ein Programm
    // es abgeschaltet hat): Dort führt sie jede Zeile sofort aus. Davor fragt die App den Benutzer – die Seite
    // zeigt selbst keine Dialoge – und schickt den Text mit `confirmed` zurück, wenn er zustimmt.
    function pasteInto(term, text, id, confirmed) {
        const clean = cleanPaste(text);
        if (clean.length === 0) {
            return;
        }

        if (!confirmed && clean.includes('\n') && !term.modes.bracketedPasteMode) {
            post({ type: 'confirmPaste', id, data: clean });
            return;
        }

        term.paste(clean);
    }

    // Farbschema, Schriftgröße und Tastenkürzel – für alle Terminals, auch die künftigen.
    function applyOptions(message) {
        if (message.theme && typeof message.theme === 'object') {
            theme = message.theme;
            document.body.style.background = theme.background;
            root.style.setProperty('--accent', theme.blue || theme.cursor);
            root.style.setProperty('--foreground', theme.foreground);
        }

        if (Number.isInteger(message.fontSize)) {
            fontSize = message.fontSize;
        }

        // Nur ein schlichter Name (die App prüft ihn schon, hier noch einmal): Er steht gleich in einer CSS-Angabe.
        if (typeof message.fontFamily === 'string' && /^[\p{L}\p{N} .-]{1,64}$/u.test(message.fontFamily)) {
            fontFamily = `"${message.fontFamily}", "Cascadia Mono", Consolas, monospace`;
        }

        if (typeof message.mouseCopyPaste === 'boolean') {
            mouseCopyPaste = message.mouseCopyPaste;
        }

        // Nur Werte, die es gibt: Die Seite verlässt sich nicht darauf, dass die App geprüft hat.
        if (message.look && typeof message.look === 'object') {
            const wanted = message.look;
            look = {
                cursorStyle: ['block', 'bar', 'underline'].includes(wanted.cursorStyle) ? wanted.cursorStyle : 'block',
                cursorBlink: wanted.cursorBlink !== false,
                lineHeight: Number.isFinite(wanted.lineHeight) ? Math.min(2, Math.max(1, wanted.lineHeight)) : 1,
                letterSpacing: Number.isInteger(wanted.letterSpacing) ? Math.min(8, Math.max(0, wanted.letterSpacing)) : 0,
                ligatures: wanted.ligatures === true,
            };
            root.classList.toggle('ligatures', look.ligatures);
        }

        if (typeof message.images === 'boolean' && message.images !== images) {
            images = message.images;
            loadImageAddon(() => {
                for (const entry of terminals.values()) {
                    applyImages(entry);
                }
            });
        }

        if (typeof message.pasteClean === 'boolean') {
            pasteClean = message.pasteClean;
        }

        if (typeof message.pasteTabs === 'boolean') {
            pasteTabs = message.pasteTabs;
        }

        if (typeof message.linkHint === 'string') {
            linkHint = message.linkHint;
        }

        if (Array.isArray(message.highlights)) {
            highlights = message.highlights.map(compileHighlight).filter(rule => rule);
        }

        if (Array.isArray(message.suggestions)) {
            suggestions = message.suggestions.filter(item => item && typeof item.name === 'string' && typeof item.text === 'string' && item.text);
            forgetTyped();
        }

        if (Array.isArray(message.hosts)) {
            hosts = plainWords(message.hosts, WORDS_MAX);
        }

        if (typeof message.tabCompletes === 'boolean') {
            tabCompletes = message.tabCompletes;
        }

        if (message.completions && typeof message.completions === 'object') {
            loadCompletions(message.completions);
            forgetTyped();
        }

        if (Array.isArray(message.shortcuts)) {
            shortcuts = message.shortcuts;
        }

        for (const entry of terminals.values()) {
            paint(entry);
            entry.term.options.fontSize = fontSize;
            entry.term.options.fontFamily = fontFamily;
            entry.term.options.cursorStyle = look.cursorStyle;
            entry.term.options.cursorBlink = look.cursorBlink;
            entry.term.options.lineHeight = look.lineHeight;
            entry.term.options.letterSpacing = look.letterSpacing;
        }

        queueFit();
    }

    // Eine Web-Adresse: http oder https, bis zum nächsten Leerzeichen oder einem Zeichen, das in keiner steht.
    const LINK_PATTERN = /https?:\/\/[^\s<>"'`{}|\\^\u0000-\u001f\u007f]+/g;

    // Ein absoluter Pfad wie /var/log/nginx/error.log – nicht mitten in einem Wort und nicht hinter einem
    // anderen Pfad. Namen aus Buchstaben, Ziffern und den üblichen Zeichen; Leerzeichen beenden ihn.
    const PATH_PATTERN = /(?<![\w.~:/@-])\/(?:[\w.@+%=,-]+\/)*[\w.@+%=,-]+\/?/g;

    // Ein relativer Pfad: ./src/main.rs, ../etc, ~/.config – oder verzeichnis/…/datei.endung, wie Compiler und
    // git ihn nennen. Ohne Endung wäre jedes „und/oder“ ein Pfad. Wohin er führt, entscheidet die App: Sie kennt
    // das Verzeichnis der Shell (OSC 7) und das Startverzeichnis.
    const RELATIVE_PATTERN = /(?<![\w.~:/@-])(?:(?:\.\.?|~)\/(?:[\w.@+%=,-]+\/)*[\w.@+%=,-]+\/?|[A-Za-z_][\w.@+-]*\/(?:[\w.@+%=,-]+\/)*[\w@+%=,-]+\.[A-Za-z]\w{0,7})/g;

    // Über so viele Zeilen des Terminals wird eine umbrochene Zeile höchstens zusammengesetzt.
    const MAX_WRAPPED_ROWS = 20;

    // Die anklickbaren Stellen eines Textes: {type: 'link' | 'path', text, first, last} mit Stellen im Text ab 0.
    function linksIn(text) {
        const found = [];
        const add = (type, index, value) => {
            if (value.length > 1 && !found.some(other => index <= other.last && index + value.length - 1 >= other.first)) {
                found.push({ type, text: value, first: index, last: index + value.length - 1 });
            }
        };

        for (const match of text.matchAll(LINK_PATTERN)) {
            // Satzzeichen am Ende gehören zum Satz, nicht zur Adresse; eine schließende Klammer nur, wenn sie
            // darin auch aufgeht.
            let value = match[0].replace(/[.,;:!?'"]+$/, '');
            while (value.endsWith(')') && !value.includes('(')) {
                value = value.slice(0, -1).replace(/[.,;:!?'"]+$/, '');
            }

            add('link', match.index, value);
        }

        for (const pattern of [PATH_PATTERN, RELATIVE_PATTERN]) {
            for (const match of text.matchAll(pattern)) {
                add('path', match.index, match[0].replace(/[.,]+$/, ''));
            }
        }

        return found;
    }

    function forgetTyped() {
        typed = '';
        offered = [];
        suggestionList.hidden = true;
    }

    // Die Tabellen und Programmlisten der App. Sie stammen aus ihr selbst, werden hier aber wie jede Nachricht
    // geprüft: nur kurze Wörter aus druckbarem ASCII, in begrenzter Zahl.
    function loadCompletions(message) {
        const word = value => typeof value === 'string' && /^[\x21-\x7e]{1,40}$/.test(value);
        const words = list => (Array.isArray(list) ? list : []).filter(word).slice(0, 200);
        tables = new Map();
        for (const table of (Array.isArray(message.tables) ? message.tables : []).slice(0, 50)) {
            if (!table || !word(table.command)) {
                continue;
            }

            const subFlags = new Map();
            if (table.subFlags && typeof table.subFlags === 'object') {
                for (const sub of Object.keys(table.subFlags).filter(word).slice(0, 100)) {
                    subFlags.set(sub, words(table.subFlags[sub]));
                }
            }

            tables.set(table.command, { subs: words(table.subs), flags: words(table.flags), subFlags, pathSubs: words(table.pathSubs) });
        }

        pathCommands = new Set(words(message.pathCommands));
        dirCommands = new Set(words(message.dirCommands));
    }

    // Die Namen im Verzeichnis der Shell eines Terminals, von der App gelesen. Sie kommen vom Server: nur Text ohne
    // Steuerzeichen und ohne Zeichen, die die Schreibrichtung drehen, in begrenzter Zahl und Länge. Eine leere
    // Liste heißt: neue Verbindung oder abgeschaltet – dann gilt auch nicht mehr, was über die Shell bekannt war.
    function setNames(id, list) {
        const entry = terminals.get(id);
        if (!entry) {
            return;
        }

        // Kein Name mit `\`: In einfachen Anführungszeichen liest ihn fish als Fluchtzeichen, die anderen Shells
        // nicht – es gibt keine Schreibweise, die überall dasselbe heißt. Bei einigen Ordnern schickt die App den
        // Inhalt mit (`children`, eine Ebene, nicht tiefer) – geprüft wie die Namen selbst.
        const valid = (items, limit, deeper) => (Array.isArray(items) ? items : [])
            .filter(item => item && typeof item.name === 'string' && item.name.length > 0 && item.name.length <= 255
                && !CONTROL.test(item.name) && !item.name.includes('/') && !item.name.includes('\\'))
            .slice(0, limit)
            .map(item => ({ name: item.name, dir: item.dir === true, children: deeper && item.dir === true ? valid(item.children, CHILDREN_MAX, false) : [] }));
        const names = valid(list, NAMES_MAX, true);
        if (names.length === 0) {
            entry.cwdSeen = false;
            entry.moved = false;
            entry.names = [];
        } else if (!entry.moved || entry.cwdSeen) {
            // Hat die Shell ihr Verzeichnis gewechselt, ohne es zu melden, passt eine Liste der App nicht mehr sicher.
            entry.names = names;
        }

        if (typedId === id) {
            offer(entry);
        }
    }

    // Wörter, die ohne Anführungszeichen getippt werden – Programme, Dienste, Container, Hosts: nur druckbares
    // ASCII ohne alles, was eine Shell deutet, vorn ein Buchstabe oder eine Ziffer. Die App hat sie schon geprüft;
    // was hier trotzdem nicht passt, fällt weg.
    function plainWords(list, limit) {
        return (Array.isArray(list) ? list : [])
            .filter(word => typeof word === 'string' && /^[A-Za-z0-9][A-Za-z0-9._+@:-]{0,254}$/.test(word))
            .slice(0, limit);
    }

    // Die Programme im Suchpfad des Servers eines Terminals (von der App nach dem Verbinden geholt).
    function setCommands(id, list) {
        const entry = terminals.get(id);
        if (entry) {
            entry.commands = plainWords(list, WORDS_MAX);
            reoffer(id, entry);
        }
    }

    // Eine Liste hat sich geändert: Was gerade vorgeschlagen wird, soll aus der neuen stammen – ein Vorschlag aus
    // einer zurückgenommenen Liste ließe sich sonst noch annehmen (Hinweis von Codex).
    function reoffer(id, entry) {
        if (typedId === id && typed) {
            offer(entry);
        }
    }

    // Merkt sich, was getippt wird, solange es gewöhnliche Zeichen sind, und zeigt, was dazu passt: gespeicherte
    // Befehle, Unterbefehle und Schalter bekannter Programme, Namen aus dem Verzeichnis der Shell. Alles andere –
    // Enter, Pfeiltasten, Strg+C, Eingefügtes – leert es: Dann stimmt die Zeile der Shell nicht mehr sicher mit
    // dem Gemerkten überein. Im zweiten Bildschirm (vim) wird nichts vorgeschlagen.
    function noteTyped(id, entry, data) {
        const nothing = suggestions.length === 0 && tables.size === 0 && pathCommands.size === 0 && hosts.length === 0
            && entry.commands.length === 0;
        if (nothing || entry.term.buffer.active.type !== 'normal') {
            forgetTyped();
            return;
        }

        if (typedId !== id) {
            typed = '';
            typedId = id;
        }

        if (data === '\r') {
            entered(entry, typed);
        }

        if (data === '\x7f') {
            typed = Array.from(typed).slice(0, -1).join('');
        } else if (Array.from(data).length === 1 && data >= ' ') {
            typed += data;
        } else {
            typed = '';
        }

        offer(entry);
    }

    // Eine Zeile wurde abgeschickt. Wechselt sie das Verzeichnis und meldet die Shell ihres nicht selbst (OSC 7),
    // weiß ab jetzt niemand mehr, wo sie steht: Dann gibt es keine Namen mehr, statt falscher.
    function entered(entry, line) {
        const words = line.trim().split(/\s+/);
        const command = words[0] === 'sudo' ? words[1] : words[0];
        if (!entry.cwdSeen && (command === 'cd' || command === 'pushd' || command === 'popd')) {
            entry.moved = true;
            entry.names = [];
        }
    }

    // Stellt zusammen, was zum Getippten passt: zuerst die gespeicherten Befehle (Tab), dann das Ergänzen (→).
    function offer(entry) {
        const start = typed.trimStart().toLowerCase();
        const saved = start.length < SUGGESTION_MIN
            ? []
            : suggestions
                .filter(item => (item.text.toLowerCase().startsWith(start) && item.text.length > start.length) || item.name.toLowerCase().startsWith(start))
                .slice(0, SUGGESTIONS_SHOWN)
                .map(item => ({ name: item.name, text: item.text.split(/\r?\n/, 1)[0], insert: item.text, back: Array.from(typed).length, key: 'Tab' }));
        offered = saved.concat(complete(entry).slice(0, COMPLETIONS_SHOWN));
        showSuggestions(entry);
    }

    // Ob die Zeile mit dem Cursor zeigt, was getippt wurde. Zeigt sie es nicht, fragt gerade etwas ohne Echo
    // (ein Passwort) oder die Shell hat die Zeile selbst umgeschrieben – dann wird nichts ergänzt.
    function echoed(entry) {
        const buffer = entry.term.buffer.active;
        const line = buffer.getLine(buffer.baseY + buffer.cursorY);
        return !!line && line.translateToString(false, 0, buffer.cursorX).endsWith(typed);
    }

    // Was das Wort unter dem Cursor ergänzen könnte. Die Zeile wird nur grob gelesen – Wörter durch Leerzeichen
    // getrennt, der Befehl nach dem letzten `|`, `;` oder `&`, ein `sudo` davor zählt nicht. Steht etwas darin, das
    // die Shell anders liest (Anführungszeichen, `\`, `$`), wird nichts vorgeschlagen, statt etwas Falsches.
    function complete(entry) {
        const anything = tables.size > 0 || pathCommands.size > 0 || entry.commands.length > 0 || hosts.length > 0;
        if (!anything || typed.length < 2 || /["'\\$`]/.test(typed) || !echoed(entry)) {
            return [];
        }

        let line = typed;
        const cut = Math.max(line.lastIndexOf('|'), line.lastIndexOf(';'), line.lastIndexOf('&'));
        if (cut >= 0) {
            line = line.slice(cut + 1);
        }

        const parts = line.replace(/^ +/, '').split(' ');
        const last = parts[parts.length - 1];
        const words = parts.slice(0, -1).filter(part => part);
        if (words[0] === 'sudo') {
            words.shift();
        }

        // Das Wort unter dem Cursor wird mit Rücktasten zurückgenommen, eine je Zeichen. Bei zusammengesetzten
        // Zeichen (Buchstabe + Akzent) nimmt der Zeileneditor mit einer Rücktaste beide – dann stimmte die Zahl
        // nicht, und es fiele mehr weg als das Wort. Deshalb nur, solange es aus ASCII besteht (Hinweis von Codex).
        if (!last || /[^\x21-\x7e]/.test(last)) {
            return [];
        }

        const back = Array.from(last).length;
        const pick = (candidates, tail, stem = last, count = back) => Array.from(new Set(candidates))
            .filter(candidate => candidate.startsWith(stem) && candidate !== stem)
            .map(candidate => ({ name: '', text: candidate, insert: candidate + (candidate.endsWith('=') ? '' : tail), back: count, key: '\u2192' }));

        // Das erste Wort der Zeile: ein Programm des Servers (die Liste hat die App nach dem Verbinden geholt) oder
        // etwas, das die Shell selbst kennt. Erst ab zwei Zeichen – sonst stünde bei jedem Buchstaben eine Liste da.
        if (words.length === 0) {
            return entry.commands.length > 0 && back >= 2 && !last.includes('/') ? pick(entry.commands.concat(BUILTINS).sort(), ' ') : [];
        }

        const command = words[0];
        const table = tables.get(command);
        let found = [];

        // Namen, die die App schon kennt: die Adressen der gespeicherten Server.
        if (!last.startsWith('-') && hosts.length > 0 && HOST_COMMANDS.has(command)) {
            // Hinter `benutzer@` zählt nur der Teil danach.
            const stem = last.slice(last.lastIndexOf('@') + 1);
            if (stem) {
                found = pick(hosts, command === 'scp' || command === 'rsync' ? ':' : ' ', stem, Array.from(stem).length);
            }
        }

        if (found.length === 0 && table) {
            const extra = (words.length >= 2 && table.subFlags.get(words[1])) || [];
            let candidates = [];
            if (last.startsWith('-')) {
                candidates = extra.filter(flag => flag.startsWith('-')).concat(table.flags);
            } else if (words.length === 1) {
                candidates = table.subs;
            } else if (words.length === 2) {
                candidates = extra.filter(word => !word.startsWith('-'));
            }

            found = pick(candidates, ' ');
        }

        // Namen aus dem Verzeichnis der Shell: hinter Programmen, die Dateien nehmen. Eine Ebene tiefer nur dort,
        // wo die App den Inhalt eines Unterverzeichnisses gleich mitgeschickt hat – fragen kann die Seite sie
        // nicht, ohne zu sagen, was getippt wurde.
        const wantsPath = pathCommands.has(command) || (table && words.length >= 2 && table.pathSubs.includes(words[1])) || last.startsWith('./');
        if (found.length === 0 && pathCommands.size > 0 && wantsPath && !last.startsWith('-') && entry.names.length > 0) {
            const prefix = last.startsWith('./') ? './' : '';
            const stem = last.slice(prefix.length);
            const slash = stem.indexOf('/');
            const dirsOnly = dirCommands.has(command);
            const matching = (list, start) => {
                const fits = dirsOnly ? list.filter(item => item.dir) : list;
                const shown = item => start.startsWith('.') || !item.name.startsWith('.');
                const hits = fits.filter(item => shown(item) && item.name.startsWith(start));
                const lower = start.toLowerCase();
                return hits.length > 0 || !start ? hits : fits.filter(item => shown(item) && item.name.toLowerCase().startsWith(lower));
            };
            // `count`: so viele Zeichen werden zur\u00fcckgenommen \u2013 genau das, was der eingesetzte Text ersetzt.
            const offerNames = (hits, start, lead, count) => hits
                .filter(item => item.dir || item.name !== start)
                .map(item => ({ name: '', text: item.name + (item.dir ? '/' : ''), insert: quoted(item.name, lead) + (item.dir ? '/' : ' '), back: count, key: '\u2192' }));
            if (stem && slash < 0) {
                // Das ganze Wort wird ersetzt, samt einem `./` davor (das steht im Eingesetzten wieder da \u2013
                // Hinweis von Codex: sonst st\u00fcnde es doppelt).
                found = offerNames(matching(entry.names, stem), stem, prefix || null, back);
            } else if (slash > 0 && stem.indexOf('/', slash + 1) < 0) {
                // `ordner/an…`: Der Ordner steht schon da; ersetzt wird nur, was hinter dem Schrägstrich getippt ist.
                const parent = entry.names.find(item => item.dir && item.name === stem.slice(0, slash) && item.children.length > 0);
                if (parent) {
                    const rest = stem.slice(slash + 1);
                    found = offerNames(matching(parent.children, rest), rest, '', Array.from(rest).length);
                }
            }
        }

        return found;
    }

    // Ein Name, wie die Shell ihn als ein Wort liest: schlichte Namen so, wie sie sind, alles andere in einfachen
    // Anführungszeichen (in denen nur das Anführungszeichen selbst eine Bedeutung hat). `lead` steht davor
    // (`./`); ohne Angabe (null) bekommt ein Name, der mit `-` beginnt, `./` davor – sonst läse ihn das Programm
    // als Schalter. Hinter einem Ordner (`lead` leer) braucht es das nicht.
    function quoted(name, lead) {
        // Schlicht ist ein Name nur, wenn auch sein erstes Zeichen nichts bedeutet (`=ls` und `~x` erweitert zsh
        // bzw. jede Shell, `%1` ist ein Job, `#` ein Kommentar).
        const before = lead ?? (name.startsWith('-') ? './' : '');
        return before + (/^[A-Za-z0-9._][A-Za-z0-9._+@%,:=-]*$/.test(name) ? name : "'" + name.replace(/'/g, "'\\''") + "'");
    }

    // Die Liste steht über der Zeile mit dem Cursor (darunter, wenn oben kein Platz ist). Namen und Befehle –
    // die des Benutzers wie die vom Server – werden als reiner Text gesetzt.
    function showSuggestions(entry) {
        suggestionList.hidden = offered.length === 0;
        const screen = entry.surface.querySelector('.xterm-screen');
        if (suggestionList.hidden || !screen) {
            suggestionList.hidden = true;
            return;
        }

        const keys = new Set();
        suggestionList.replaceChildren(...offered.map((item, index) => {
            const row = document.createElement('div');
            row.className = index === 0 ? 'suggestion first' : 'suggestion';
            if (item.name) {
                const name = document.createElement('span');
                name.className = 'suggestion-name';
                name.textContent = item.name;
                row.append(name);
            }

            const text = document.createElement('span');
            text.className = 'suggestion-text';
            text.textContent = item.text.slice(0, 200);
            row.append(text);

            // Die Taste steht am ersten Vorschlag ihrer Art: Tab nimmt den ersten gespeicherten Befehl, → die
            // erste Ergänzung.
            if (!keys.has(item.key)) {
                keys.add(item.key);
                const key = document.createElement('span');
                key.className = 'suggestion-key';
                key.textContent = item.key;
                row.append(key);
            }

            return row;
        }));

        const box = screen.getBoundingClientRect();
        const buffer = entry.term.buffer.active;
        const cellWidth = box.width / entry.term.cols;
        const cellHeight = box.height / entry.term.rows;
        const top = box.top + buffer.cursorY * cellHeight;
        const height = suggestionList.offsetHeight;
        suggestionList.style.left = Math.max(4, Math.min(box.left + buffer.cursorX * cellWidth, window.innerWidth - suggestionList.offsetWidth - 4)) + 'px';
        suggestionList.style.top = (top - height - 4 >= 0 ? top - height - 4 : top + cellHeight + 4) + 'px';
    }

    // Übernimmt den ersten Vorschlag zu dieser Taste. Tab: ein gespeicherter Befehl ersetzt die Zeile. →: eine
    // Ergänzung ersetzt das Wort unter dem Cursor. Getippt wird ohne Enter – das drückt der Benutzer selbst.
    function acceptSuggestion(key) {
        const item = offered.find(candidate => candidate.key === key);
        if (!item) {
            return false;
        }

        // Nie ein Zeilenumbruch und nie ein Steuerzeichen: Zu sehen ist nur die erste Zeile, also wird auch nur
        // sie getippt – sonst liefe ein mehrzeiliger Befehl (etwa aus einer importierten Datei) mit einem Tab los.
        const text = item.insert.split(/\r?\n/)[0].replace(/[\u0000-\u001f\u007f-\u009f]/g, '');
        post({ type: 'input', id: typedId, data: '\x7f'.repeat(item.back) + text });
        if (key === 'Tab') {
            forgetTyped();
        } else {
            // Die Zeile geht weiter: Was jetzt dasteht, ist bekannt – der nächste Schalter lässt sich ergänzen.
            const kept = Array.from(typed);
            typed = kept.slice(0, kept.length - item.back).join('') + text;
            offered = [];
            suggestionList.hidden = true;
        }

        return true;
    }

    // Eine Regel der App als Ausdruck. Die App hat sie geprüft; was hier trotzdem kein Ausdruck ist, fällt weg.
    function compileHighlight(rule) {
        if (!rule || typeof rule.pattern !== 'string' || !rule.pattern || typeof rule.color !== 'string') {
            return null;
        }

        try {
            const source = rule.regex ? rule.pattern : rule.pattern.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
            return { expression: new RegExp(source, rule.ignoreCase ? 'gi' : 'g'), color: rule.color };
        } catch {
            return null;
        }
    }

    // Text einer Zeile und zu jedem Zeichen darin die Spalte – breite Zeichen belegen zwei.
    function cellsOf(line, cols) {
        let text = '';
        const columns = [];
        for (let x = 0; x < cols; x++) {
            const cell = line.getCell(x);
            if (!cell || cell.getWidth() === 0) {
                continue;
            }

            const chars = cell.getChars() || ' ';
            for (let index = 0; index < chars.length; index++) {
                columns.push(x);
            }

            text += chars;
        }

        return { text, columns };
    }

    // Färbt, was seit dem letzten Mal dazugekommen ist. Die Zeile mit dem Cursor ist noch nicht fertig: Ihre
    // Hervorhebungen fallen beim nächsten Mal wieder weg und entstehen neu. Im zweiten Bildschirm (vim, htop)
    // wird nichts gefärbt, und nach einer Flut nur das Ende.
    function highlight(entry) {
        const term = entry.term;
        const buffer = term.buffer.active;
        if (buffer.type !== 'normal') {
            return;
        }

        for (const decoration of entry.fresh) {
            decoration.dispose();
        }

        entry.fresh = [];
        if (highlights.length === 0) {
            entry.mark?.dispose();
            entry.mark = null;
            return;
        }

        // Ohne Marke (erste Ausgabe, oder die Zeile ist aus dem Verlauf gefallen) zählt das Ende des Verlaufs.
        const last = buffer.baseY + buffer.cursorY;
        const known = entry.mark && entry.mark.line >= 0 ? entry.mark.line : 0;
        const first = Math.max(0, Math.min(known, last), last - HIGHLIGHT_LINES);
        for (let row = first; row <= last; row++) {
            const line = buffer.getLine(row);
            if (!line) {
                continue;
            }

            const { text, columns } = cellsOf(line, term.cols);
            for (const rule of highlights) {
                rule.expression.lastIndex = 0;
                let match;
                let count = 0;
                while ((match = rule.expression.exec(text)) !== null && count++ < HIGHLIGHT_MATCHES) {
                    if (match[0].length === 0) {
                        rule.expression.lastIndex++;
                        continue;
                    }

                    const start = columns[match.index];
                    const end = columns[match.index + match[0].length - 1];
                    const marker = term.registerMarker(row - last);
                    const decoration = marker && term.registerDecoration({
                        marker,
                        x: start,
                        width: end - start + (line.getCell(end)?.getWidth() || 1),
                        foregroundColor: highlightColor(rule.color),
                        layer: 'top',
                    });
                    if (decoration) {
                        decoration.onDispose(() => marker.dispose());
                        (row === last ? entry.fresh : entry.colored).push(decoration);
                    }
                }
            }
        }

        while (entry.colored.length > HIGHLIGHT_DECORATIONS) {
            entry.colored.shift().dispose();
        }

        entry.mark?.dispose();
        entry.mark = term.registerMarker(0);
    }

    // Die Farbe kommt aus dem Farbschema – die helle Variante, sie hebt sich vom gewöhnlichen Text ab.
    function highlightColor(name) {
        const bright = 'bright' + name.charAt(0).toUpperCase() + name.slice(1);
        const color = theme[bright] || theme[name];
        return /^#[0-9a-f]{6}$/i.test(color || '') ? color : '#ffff00';
    }

    // Die erste Größenmeldung startet in der App die Sitzung – sie muss also auch kommen, wenn das Terminal
    // zufällig schon die Standardgröße hat und xterm deshalb kein onResize auslöst.
    function reportSize(id, entry) {
        if (holdSizes) {
            return;
        }

        const { cols, rows } = entry.term;
        if (cols !== entry.cols || rows !== entry.rows) {
            entry.cols = cols;
            entry.rows = rows;
            post({ type: 'resize', id, cols, rows });
        }
    }

    // Ein Zeichen, das ein Programm als Unicode-Paket schickt statt als Taste (VK_PACKET): Texterweiterungen,
    // Auto-Type von Passwortmanagern, Fernsteuerungen.
    const VK_PACKET = 231;

    // Tasten, die ohne Scancode ankommen (Fernsteuerung, Makro-Programme), haben kein event.code; dann sagt der
    // Tastencode, welche Taste es war.
    const CODES_BY_KEY = {
        9: 'Tab', 33: 'PageUp', 34: 'PageDown', 37: 'ArrowLeft', 38: 'ArrowUp', 39: 'ArrowRight', 40: 'ArrowDown',
        96: 'Digit0', 107: 'Equal', 109: 'Minus', 187: 'Equal', 189: 'Minus',
        112: 'F1', 113: 'F2', 114: 'F3', 115: 'F4', 116: 'F5', 117: 'F6', 118: 'F7', 119: 'F8', 120: 'F9', 121: 'F10', 122: 'F11', 123: 'F12',
    };

    function codeOf(event) {
        if (event.code) {
            return event.code === 'NumpadAdd' ? 'Equal' : event.code === 'NumpadSubtract' ? 'Minus' : event.code === 'Numpad0' ? 'Digit0' : event.code;
        }

        const key = event.keyCode;
        if (key >= 65 && key <= 90) {
            return 'Key' + String.fromCharCode(key);
        }

        return key >= 48 && key <= 57 ? 'Digit' + String.fromCharCode(key) : CODES_BY_KEY[key] ?? '';
    }

    // Gibt false zurück, wenn xterm die Taste nicht verarbeiten soll.
    function handleKey(term, event) {
        // Für ein Unicode-Paket nennt der Browser im keydown eine falsche Taste; xterm schickte sie und verschluckte
        // das Zeichen. Ohne xterm kommt das Zeichen danach als gewöhnliche Eingabe an.
        if (event.keyCode === VK_PACKET) {
            return event.type !== 'keydown';
        }

        // Steht ein Vorschlag da, gehört Tab ihm – sonst wie immer der Shell.
        // Das gilt nur den gespeicherten Befehlen. Eine Ergänzung (Unterbefehl, Schalter, Name) nimmt die Pfeiltaste
        // nach rechts: Tab bleibt dort der Shell und ihrer eigenen Vervollständigung. Am Ende der Zeile – und nur
        // dort steht der Cursor, solange ein Vorschlag da ist – tut die Pfeiltaste in der Shell sonst nichts.
        if (event.type === 'keydown' && offered.length > 0 && typedId === activeId
            && !event.ctrlKey && !event.altKey && !event.shiftKey && !event.metaKey) {
            // Auf Wunsch (Einstellung) nimmt Tab auch eine Ergänzung, wenn kein gespeicherter Befehl dasteht.
            const code = codeOf(event);
            const taken = code === 'Tab' ? acceptSuggestion('Tab') || (tabCompletes && acceptSuggestion('→'))
                : code === 'ArrowRight' && acceptSuggestion('→');
            if (taken) {
                event.preventDefault();
                return false;
            }
        }

        // F11 allein gehört der App: nur das Terminal, bildschirmfüllend – wie im Browser und in Windows Terminal.
        if (event.type === 'keydown' && codeOf(event) === 'F11'
            && !event.ctrlKey && !event.altKey && !event.shiftKey && !event.metaKey) {
            event.preventDefault();
            post({ type: 'shortcut', key: 'view.terminalOnly' });
            return false;
        }

        if (event.type !== 'keydown' || !event.ctrlKey || event.altKey || event.metaKey) {
            return true;
        }

        // Der WebView reicht Tasten nicht an XAML weiter, deshalb meldet die Seite die Kürzel der App selbst.
        // Welche das sind, schickt die App (ShellViewModel.Commands). Die zum Nachbarn springen nur in einer Teilung.
        const code = codeOf(event);
        const shortcut = shortcuts.find(candidate => candidate.code === code && candidate.shift === event.shiftKey);
        if (shortcut && (!NEIGHBOR_COMMANDS.has(shortcut.id) || leavesOf(tree).length > 1)) {
            event.preventDefault();
            post({ type: 'shortcut', key: shortcut.id });
            return false;
        }

        // Wie Windows Terminal: Strg+C kopiert, wenn etwas markiert ist, sonst geht ^C an den Server.
        if (code === 'KeyC' && (event.shiftKey || term.hasSelection())) {
            event.preventDefault();
            if (term.hasSelection()) {
                navigator.clipboard.writeText(term.getSelection()).catch(() => { });
                term.clearSelection();
            }

            return false;
        }

        // Einfügen dem Browser überlassen: Er löst ein paste-Ereignis aus, das xterm selbst verarbeitet.
        if (code === 'KeyV') {
            return false;
        }

        return true;
    }

    // --- Suche ---

    function searchOptions(message) {
        return {
            caseSensitive: message.matchCase === true,
            incremental: message.incremental === true,
            decorations: {
                matchBackground: '#5a4a00',
                matchOverviewRuler: '#c19c00',
                activeMatchBackground: '#c19c00',
                activeMatchColorOverviewRuler: '#f9f1a5',
            },
        };
    }

    function find(message) {
        const entry = terminals.get(message.id);
        if (!entry || typeof message.data !== 'string' || message.data.length === 0) {
            return;
        }

        if (message.backwards === true) {
            entry.search.findPrevious(message.data, searchOptions(message));
        } else {
            entry.search.findNext(message.data, searchOptions(message));
        }
    }

    function findClear(id) {
        const entry = terminals.get(id);
        if (entry) {
            entry.search.clearDecorations();
            entry.term.clearSelection();
        }
    }

    // Zeile des Verlaufs als Text. Eine umbrochene Zeile zählt mit ihrer ersten Bildschirmzeile.
    function lineText(buffer, row) {
        let text = buffer.getLine(row).translateToString(true);
        for (let next = row + 1; next < buffer.length && buffer.getLine(next).isWrapped; next++) {
            text += buffer.getLine(next).translateToString(true);
        }

        return text;
    }

    function matches(text, query, matchCase) {
        return matchCase ? text.indexOf(query) : text.toLowerCase().indexOf(query.toLowerCase());
    }

    // Alle Zeilen aller Terminals, in denen der Suchtext vorkommt – für die Trefferliste der App.
    function grep(message) {
        const query = typeof message.data === 'string' ? message.data : '';
        const matchCase = message.matchCase === true;
        const items = [];
        let more = false;
        if (query.length > 0) {
            scan: for (const [id, entry] of terminals) {
                const buffer = entry.term.buffer.active;
                for (let row = 0; row < buffer.length; row++) {
                    const line = buffer.getLine(row);
                    if (!line || line.isWrapped) {
                        continue;
                    }

                    const text = lineText(buffer, row);
                    if (matches(text, query, matchCase) >= 0) {
                        if (items.length === MAX_HITS) {
                            more = true;
                            break scan;
                        }

                        items.push({ id, row, text: text.slice(0, MAX_HIT_LENGTH) });
                    }
                }
            }
        }

        post({ type: 'hits', data: query, items, more });
    }

    // Rollt zu einer Zeile aus der Trefferliste und markiert die Fundstelle darin.
    function reveal(message) {
        const entry = terminals.get(message.id);
        if (!entry || !Number.isInteger(message.row) || typeof message.data !== 'string') {
            return;
        }

        const buffer = entry.term.buffer.active;
        const row = Math.min(Math.max(message.row, 0), buffer.length - 1);
        const column = Math.max(matches(lineText(buffer, row), message.data, message.matchCase === true), 0);
        entry.term.scrollToLine(Math.max(row - Math.floor(entry.term.rows / 2), 0));
        entry.term.select(column % entry.term.cols, row + Math.floor(column / entry.term.cols), message.data.length);
    }

    function clear(id) {
        const entry = terminals.get(id);
        if (entry) {
            // Bildschirm und Verlauf leeren; die Zeile mit dem Prompt bleibt stehen.
            entry.term.clear();
            entry.term.focus();
        }
    }

    // Der ganze Text des Terminals – Verlauf und Bildschirm, ohne Farben –, damit die App ihn speichern kann.
    // Gemeint ist der normale Puffer: Läuft gerade ein Vollbildprogramm (vim, htop), zählt, was darunter steht.
    function dump(id) {
        const entry = terminals.get(id);
        if (!entry) {
            return;
        }

        const buffer = entry.term.buffer.normal;
        const lines = [];
        for (let row = 0; row < buffer.length; row++) {
            const line = buffer.getLine(row);
            if (line && !line.isWrapped) {
                lines.push(lineText(buffer, row));
            }
        }

        while (lines.length > 0 && lines[lines.length - 1].length === 0) {
            lines.pop();
        }

        // Passt es nicht, zählt das Ende – dort steht das Neueste.
        post({ type: 'text', id, data: lines.join('\n').slice(-MAX_DUMP_LENGTH) });
    }

    // Der ganze Inhalt eines Terminals samt Farben, Cursor und Modi, als Text mit Steuerzeichen: Zieht der Tab in ein
    // anderes Fenster um, läuft dort eine zweite Seite, und die bekommt ihn als Erstes geschrieben. Das leere
    // Schreiben davor wartet, bis alles Frühere verarbeitet ist. Geantwortet wird immer – die App wartet darauf.
    function snapshot(id) {
        const entry = terminals.get(id);
        if (!entry) {
            post({ type: 'snapshot', id, data: '' });
            return;
        }

        entry.term.write('', () => {
            let data = '';
            try {
                const serializer = new SerializeAddon.SerializeAddon();
                entry.term.loadAddon(serializer);
                data = serializer.serialize();
                serializer.dispose();
            } catch {
                data = '';
            }

            post({ type: 'snapshot', id, data: data.length <= MAX_SNAPSHOT_LENGTH ? data : '' });
        });
    }

    // Die Farben, die xterm nimmt, wenn ein Farbschema eine der sechzehn nicht nennt.
    const ANSI_KEYS = ['black', 'red', 'green', 'yellow', 'blue', 'magenta', 'cyan', 'white',
        'brightBlack', 'brightRed', 'brightGreen', 'brightYellow', 'brightBlue', 'brightMagenta', 'brightCyan', 'brightWhite'];
    const ANSI_DEFAULTS = ['#2e3436', '#cc0000', '#4e9a06', '#c4a000', '#3465a4', '#75507b', '#06989a', '#d3d7cf',
        '#555753', '#ef2929', '#8ae234', '#fce94f', '#729fcf', '#ad7fa8', '#34e2e2', '#eeeeec'];
    const MAX_COPY_RUNS = 20000;
    const MAX_COPY_LENGTH = 1024 * 1024;

    function hex(red, green, blue) {
        return '#' + [red, green, blue].map(part => part.toString(16).padStart(2, '0')).join('');
    }

    // Eine der 256 Farben eines Terminals als #rrggbb: die sechzehn des Farbschemas, der Würfel, die Graustufen.
    function paletteColor(colors, index) {
        if (index < 16) {
            const named = colors[ANSI_KEYS[index]];
            return typeof named === 'string' && /^#[0-9a-f]{6}$/i.test(named) ? named : ANSI_DEFAULTS[index];
        }

        if (index < 232) {
            const step = value => value === 0 ? 0 : 55 + (value * 40);
            const cube = index - 16;
            return hex(step(Math.floor(cube / 36)), step(Math.floor(cube / 6) % 6), step(cube % 6));
        }

        const gray = 8 + ((index - 232) * 10);
        return hex(gray, gray, gray);
    }

    // Was markiert ist, für die App – sie legt es in die Zwischenablage (die Seite hat dafür nicht immer den
    // Fokus, und für formatierten Text reicht ihr Zugang nicht). „line“: der Text ohne Zeilenumbrüche. „rich“:
    // dazu die Stücke gleicher Farbe und Schrift, aus denen die App HTML und RTF baut – die Seite schickt keine
    // fertige Formatierung, nur Text und Farben.
    function copySelection(id, mode) {
        const entry = terminals.get(id);
        if (!entry || !entry.term.hasSelection()) {
            post({ type: 'copied', id, key: mode, data: '' });
            return;
        }

        const term = entry.term;
        const text = term.getSelection().slice(0, MAX_COPY_LENGTH);
        if (mode !== 'rich') {
            // Wie „als eine Zeile“ im Windows Terminal: Die Umbrüche fallen weg, sonst nichts.
            post({ type: 'copied', id, key: 'line', data: text.split(/\r?\n/).map(line => line.replace(/\s+$/, '')).join('') });
            return;
        }

        const colors = entry.theme || theme;
        const range = term.getSelectionPosition();
        const buffer = term.buffer.active;
        const runs = [];
        const push = (part, style) => {
            const last = runs[runs.length - 1];
            if (last && last.f === style.f && last.b === style.b && last.s === style.s) {
                last.t += part;
            } else if (runs.length < MAX_COPY_RUNS) {
                runs.push({ t: part, f: style.f, b: style.b, s: style.s });
            }
        };
        const plain = { f: null, b: null, s: 0 };
        if (range) {
            const cell = buffer.getNullCell();
            for (let row = range.start.y; row <= range.end.y; row++) {
                const line = buffer.getLine(row);
                if (!line) {
                    continue;
                }

                // Wie getSelection: Leerraum am Ende einer Zeile zählt nicht, und eine umbrochene Zeile geht ohne
                // Umbruch weiter.
                const from = row === range.start.y ? range.start.x : 0;
                let to = row === range.end.y ? Math.min(range.end.x, line.length) : line.length;
                const next = buffer.getLine(row + 1);
                const wraps = row < range.end.y && next && next.isWrapped;
                if (!wraps) {
                    while (to > from && (line.getCell(to - 1, cell)?.getChars() || ' ').trim() === '') {
                        to--;
                    }
                }

                for (let column = from; column < to; column++) {
                    if (!line.getCell(column, cell) || cell.getWidth() === 0) {
                        continue;
                    }

                    let fore = cell.isFgRGB() ? '#' + cell.getFgColor().toString(16).padStart(6, '0')
                        : cell.isFgPalette() ? paletteColor(colors, cell.getFgColor()) : null;
                    let back = cell.isBgRGB() ? '#' + cell.getBgColor().toString(16).padStart(6, '0')
                        : cell.isBgPalette() ? paletteColor(colors, cell.getBgColor()) : null;
                    if (cell.isInverse()) {
                        [fore, back] = [back || colors.background, fore || colors.foreground];
                    }

                    push(cell.getChars() || ' ', {
                        f: fore,
                        b: back,
                        s: (cell.isBold() ? 1 : 0) | (cell.isItalic() ? 2 : 0) | (cell.isUnderline() ? 4 : 0),
                    });
                }

                if (row < range.end.y && !wraps) {
                    push('\n', plain);
                }
            }
        }

        // Stimmt der Text der Stücke nicht mit dem überein, was xterm als Auswahl nennt (eine Blockauswahl etwa),
        // geht der Text ohne Farben – lieber schlicht als falsch.
        const joined = runs.map(run => run.t).join('');
        const same = joined.replace(/\s+/g, ' ').trim() === text.replace(/\s+/g, ' ').trim();
        post({
            type: 'copied',
            id,
            key: 'rich',
            data: text,
            fore: colors.foreground,
            back: colors.background,
            runs: same ? runs : [],
        });
    }

    function queueFit() {
        if (fitQueued) {
            return;
        }

        fitQueued = true;
        requestAnimationFrame(() => {
            fitQueued = false;
            for (const id of panes) {
                const entry = terminals.get(id);
                if (entry && entry.opened) {
                    entry.fit.fit();
                }
            }
        });
    }

    new ResizeObserver(queueFit).observe(root);
    // Bekommt das WebView den Fokus, geht er ins aktive Terminal – außer eine Trennlinie hatte ihn (angeklickt oder
    // über den Befehl der App gewählt): Dann schieben weiter die Pfeiltasten.
    window.addEventListener('focus', () => {
        if (!document.activeElement?.classList.contains('divider')) {
            terminals.get(activeId)?.term.focus();
        }
    });

    // Strg+Mausrad ändert die Schriftgröße, wie Strg +/−. Abgefangen, bevor xterm scrollt; die Größe selbst setzt
    // die App (sie gilt für alle Terminals und steht in den Einstellungen).
    window.addEventListener('wheel', event => {
        if (!event.ctrlKey || event.deltaY === 0) {
            return;
        }

        event.preventDefault();
        event.stopPropagation();
        post({ type: 'shortcut', key: event.deltaY < 0 ? 'terminal.zoomIn' : 'terminal.zoomOut' });
    }, { capture: true, passive: false });

    listen(message => {
        if (!message || typeof message.type !== 'string') {
            return;
        }

        switch (message.type) {
            case 'open':
                open(message.id, message.mark, message.theme);
                break;
            case 'theme':
                setTheme(message.id, message.theme);
                break;
            case 'commands':
                setCommands(message.id, message.words);
                break;
            case 'names':
                setNames(message.id, message.names);
                break;
            case 'label':
                label(message.id, message.data, message.state, message.hint);
                break;
            case 'layout':
                layout(message.tree, message.id, message.zoom, message.fixed);
                break;
            case 'close':
                close(message.id);
                break;
            case 'write':
                write(message.id, message.data);
                break;
            case 'options':
                applyOptions(message);
                break;
            case 'backdrop':
                applyBackdrop(message);
                break;
            case 'paste':
                // Antwort der App auf einen Rechtsklick: Text der Zwischenablage, über xterm eingefügt.
                if (typeof message.data === 'string' && message.data.length > 0 && terminals.has(message.id)) {
                    pasteInto(terminals.get(message.id).term, message.data, message.id, message.confirmed === true);
                }
                break;
            case 'find':
                find(message);
                break;
            case 'findClear':
                findClear(message.id);
                break;
            case 'grep':
                grep(message);
                break;
            case 'reveal':
                reveal(message);
                break;
            case 'clear':
                clear(message.id);
                break;
            case 'divider':
                focusDivider(message.id);
                break;
            case 'dump':
                dump(message.id);
                break;
            case 'snapshot':
                snapshot(message.id);
                break;
            case 'copy':
                copySelection(message.id, message.data);
                break;
        }
    });

    post({ type: 'ready' });
})();
