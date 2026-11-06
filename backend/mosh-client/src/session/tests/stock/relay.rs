use super::*;

use std::net::{SocketAddr, SocketAddrV4};
use std::sync::{
    Arc, Mutex,
    atomic::{AtomicBool, AtomicUsize, Ordering},
};

use tokio::net::UdpSocket;

use crate::limits::MAX_DATAGRAM_BYTES;

// NexusSSH (P1): Datagramme dieser Größe tragen Teile einer Anweisung mit mehreren Fragmenten.
const LOSSY_DATAGRAM_BYTES: usize = 400;

#[derive(Clone, Copy)]
pub(super) struct RelayTraffic {
    pub(super) sent: [usize; 2],
    pub(super) received: [usize; 2],
}

#[derive(Clone)]
struct RelayControl {
    client_addr: Arc<Mutex<Option<SocketAddr>>>,
    last_server_datagram: Arc<Mutex<Option<Vec<u8>>>>,
    drop_client_to_server: Arc<AtomicBool>,
    drop_server_to_client: Arc<AtomicBool>,
    active_source: Arc<AtomicUsize>,
    dropped: Arc<AtomicUsize>,
    // NexusSSH (P1): jedes n-te Datagramm des Servers ab `LOSSY_DATAGRAM_BYTES` verwerfen (0: keines).
    drop_every_large: Arc<AtomicUsize>,
    large_seen: Arc<AtomicUsize>,
    one_way_delay: Duration,
}

pub(super) struct UdpRelay {
    endpoint: SocketAddrV4,
    downstream: Arc<UdpSocket>,
    client_addr: Arc<Mutex<Option<SocketAddr>>>,
    last_server_datagram: Arc<Mutex<Option<Vec<u8>>>>,
    drop_client_to_server: Arc<AtomicBool>,
    drop_server_to_client: Arc<AtomicBool>,
    active_source: Arc<AtomicUsize>,
    dropped: Arc<AtomicUsize>,
    drop_every_large: Arc<AtomicUsize>,
    sent: [Arc<AtomicUsize>; 2],
    received: [Arc<AtomicUsize>; 2],
    tasks: Vec<JoinHandle<()>>,
}

impl UdpRelay {
    pub(super) async fn start(server_addr: SocketAddr) -> Self {
        Self::start_with_delay(server_addr, Duration::ZERO).await
    }

    pub(super) async fn start_with_delay(server_addr: SocketAddr, one_way_delay: Duration) -> Self {
        let downstream = Arc::new(
            UdpSocket::bind((Ipv4Addr::LOCALHOST, 0))
                .await
                .expect("failed to bind relay client endpoint"),
        );
        let endpoint = socket_addr_v4(
            downstream
                .local_addr()
                .expect("relay client endpoint has no local address"),
        );
        let upstreams = [
            Arc::new(
                UdpSocket::bind((Ipv4Addr::LOCALHOST, 0))
                    .await
                    .expect("failed to bind first relay source"),
            ),
            Arc::new(
                UdpSocket::bind((Ipv4Addr::LOCALHOST, 0))
                    .await
                    .expect("failed to bind second relay source"),
            ),
        ];
        for upstream in &upstreams {
            upstream
                .connect(server_addr)
                .await
                .expect("failed to connect relay source to stock server");
        }
        assert_ne!(
            upstreams[0].local_addr().unwrap().port(),
            upstreams[1].local_addr().unwrap().port(),
            "relay sources unexpectedly share a UDP port"
        );

        let drop_client_to_server = Arc::new(AtomicBool::new(false));
        let drop_server_to_client = Arc::new(AtomicBool::new(false));
        let active_source = Arc::new(AtomicUsize::new(0));
        let dropped = Arc::new(AtomicUsize::new(0));
        let drop_every_large = Arc::new(AtomicUsize::new(0));
        let sent = [Arc::new(AtomicUsize::new(0)), Arc::new(AtomicUsize::new(0))];
        let received = [Arc::new(AtomicUsize::new(0)), Arc::new(AtomicUsize::new(0))];
        let client_addr = Arc::new(Mutex::new(None));
        let last_server_datagram = Arc::new(Mutex::new(None));
        let control = RelayControl {
            client_addr: Arc::clone(&client_addr),
            last_server_datagram: Arc::clone(&last_server_datagram),
            drop_client_to_server: Arc::clone(&drop_client_to_server),
            drop_server_to_client: Arc::clone(&drop_server_to_client),
            active_source: Arc::clone(&active_source),
            dropped: Arc::clone(&dropped),
            drop_every_large: Arc::clone(&drop_every_large),
            large_seen: Arc::new(AtomicUsize::new(0)),
            one_way_delay,
        };

        let mut tasks = Vec::with_capacity(3);
        tasks.push(tokio::spawn(forward_client_datagrams(
            Arc::clone(&downstream),
            [Arc::clone(&upstreams[0]), Arc::clone(&upstreams[1])],
            control.clone(),
            [Arc::clone(&sent[0]), Arc::clone(&sent[1])],
        )));
        for (index, upstream) in upstreams.into_iter().enumerate() {
            tasks.push(tokio::spawn(forward_server_datagrams(
                upstream,
                Arc::clone(&downstream),
                control.clone(),
                Arc::clone(&received[index]),
            )));
        }

        Self {
            endpoint,
            downstream,
            client_addr,
            last_server_datagram,
            drop_client_to_server,
            drop_server_to_client,
            active_source,
            dropped,
            drop_every_large,
            sent,
            received,
            tasks,
        }
    }

    pub(super) const fn endpoint(&self) -> SocketAddrV4 {
        self.endpoint
    }

    pub(super) fn pause(&self) {
        self.drop_client_to_server.store(true, Ordering::SeqCst);
        self.drop_server_to_client.store(true, Ordering::SeqCst);
    }

    pub(super) fn pause_client_to_server(&self) {
        self.drop_client_to_server.store(true, Ordering::SeqCst);
        self.drop_server_to_client.store(false, Ordering::SeqCst);
    }

    pub(super) fn resume(&self) {
        self.drop_client_to_server.store(false, Ordering::SeqCst);
        self.drop_server_to_client.store(false, Ordering::SeqCst);
    }

    pub(super) fn switch_source_port(&self) {
        self.active_source.store(1, Ordering::SeqCst);
    }

    // NexusSSH (P1): Verlust wie auf einer schlechten Leitung, nur vom Server zum Client.
    pub(super) fn drop_every_nth_large_server_datagram(&self, every: usize) {
        self.drop_every_large.store(every, Ordering::SeqCst);
    }

    pub(super) fn dropped_packets(&self) -> usize {
        self.dropped.load(Ordering::SeqCst)
    }

    pub(super) fn traffic(&self) -> RelayTraffic {
        RelayTraffic {
            sent: [
                self.sent[0].load(Ordering::SeqCst),
                self.sent[1].load(Ordering::SeqCst),
            ],
            received: [
                self.received[0].load(Ordering::SeqCst),
                self.received[1].load(Ordering::SeqCst),
            ],
        }
    }

    pub(super) fn total_client_datagrams(&self) -> usize {
        self.sent
            .iter()
            .map(|count| count.load(Ordering::SeqCst))
            .sum()
    }

    pub(super) fn last_server_datagram(&self) -> Vec<u8> {
        self.last_server_datagram
            .lock()
            .expect("relay server-datagram lock poisoned")
            .clone()
            .expect("relay captured no server datagram")
    }

    pub(super) async fn inject_to_client(&self, datagram: &[u8]) {
        let client = self
            .client_addr
            .lock()
            .expect("relay client lock poisoned")
            .expect("relay has not observed its client");
        let sent = self
            .downstream
            .send_to(datagram, client)
            .await
            .expect("failed to inject datagram into relay client");
        assert_eq!(sent, datagram.len(), "relay injection was incomplete");
    }
}

impl Drop for UdpRelay {
    fn drop(&mut self) {
        for task in &self.tasks {
            task.abort();
        }
    }
}

async fn forward_client_datagrams(
    downstream: Arc<UdpSocket>,
    upstreams: [Arc<UdpSocket>; 2],
    control: RelayControl,
    sent: [Arc<AtomicUsize>; 2],
) {
    let mut datagram = [0_u8; MAX_DATAGRAM_BYTES];
    while let Ok((length, source)) = downstream.recv_from(&mut datagram).await {
        {
            let mut known_client = control
                .client_addr
                .lock()
                .expect("relay client lock poisoned");
            if known_client.is_none() {
                *known_client = Some(source);
            }
            if *known_client != Some(source) {
                continue;
            }
        }
        if control.drop_client_to_server.load(Ordering::SeqCst) {
            control.dropped.fetch_add(1, Ordering::SeqCst);
            continue;
        }
        tokio::time::sleep(control.one_way_delay).await;
        let index = control.active_source.load(Ordering::SeqCst).min(1);
        if upstreams[index].send(&datagram[..length]).await.is_err() {
            break;
        }
        sent[index].fetch_add(1, Ordering::SeqCst);
    }
}

async fn forward_server_datagrams(
    upstream: Arc<UdpSocket>,
    downstream: Arc<UdpSocket>,
    control: RelayControl,
    received: Arc<AtomicUsize>,
) {
    let mut datagram = [0_u8; MAX_DATAGRAM_BYTES];
    while let Ok(length) = upstream.recv(&mut datagram).await {
        if control.drop_server_to_client.load(Ordering::SeqCst) {
            control.dropped.fetch_add(1, Ordering::SeqCst);
            continue;
        }
        let every = control.drop_every_large.load(Ordering::SeqCst);
        if every > 0
            && length >= LOSSY_DATAGRAM_BYTES
            && (control.large_seen.fetch_add(1, Ordering::SeqCst) + 1).is_multiple_of(every)
        {
            control.dropped.fetch_add(1, Ordering::SeqCst);
            continue;
        }
        let client = *control
            .client_addr
            .lock()
            .expect("relay client lock poisoned");
        let Some(client) = client else {
            continue;
        };
        *control
            .last_server_datagram
            .lock()
            .expect("relay server-datagram lock poisoned") = Some(datagram[..length].to_vec());
        tokio::time::sleep(control.one_way_delay).await;
        if downstream
            .send_to(&datagram[..length], client)
            .await
            .is_err()
        {
            break;
        }
        received.fetch_add(1, Ordering::SeqCst);
    }
}

fn socket_addr_v4(address: SocketAddr) -> SocketAddrV4 {
    match address {
        SocketAddr::V4(address) => address,
        SocketAddr::V6(_) => panic!("loopback relay unexpectedly bound an IPv6 address"),
    }
}
