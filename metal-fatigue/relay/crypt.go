package main

// Encrypted connections: the client sends cKex with "MFE1" and its X25519 public key, the relay
// answers sKex with its own, both sides derive one key per direction, and every frame after
// that is sealed with ChaCha20-Poly1305 (a 16-byte tag follows the encrypted type+payload).
// Clients that start with cHello instead stay unencrypted, so older add-ons keep working.

import (
	"crypto/ecdh"
	"crypto/rand"
	"encoding/binary"
	"errors"

	"golang.org/x/crypto/chacha20"
	"golang.org/x/crypto/chacha20poly1305"
)

const (
	cKex = 0x0B
	sKex = 0x8B
)

var kexMagic = []byte("MFE1")

type cipherState struct {
	tx, rx     [32]byte
	ntx, nrx   uint64
}

func nonceFor(n uint64) []byte {
	b := make([]byte, 12)
	binary.LittleEndian.PutUint64(b[4:], n)
	return b
}

// keyExchange answers a client's cKex payload; returns the reply payload and the cipher state.
func keyExchange(payload []byte) ([]byte, *cipherState, error) {
	if len(payload) != 36 || string(payload[:4]) != string(kexMagic) {
		return nil, nil, errors.New("bad key exchange")
	}
	peer, err := ecdh.X25519().NewPublicKey(payload[4:])
	if err != nil {
		return nil, nil, err
	}
	priv, err := ecdh.X25519().GenerateKey(rand.Reader)
	if err != nil {
		return nil, nil, err
	}
	shared, err := priv.ECDH(peer) // rejects the all-zero result
	if err != nil {
		return nil, nil, err
	}
	nonce := make([]byte, 12)
	copy(nonce, "MFE1-keys")
	c, err := chacha20.NewUnauthenticatedCipher(shared, nonce)
	if err != nil {
		return nil, nil, err
	}
	ks := make([]byte, 64)
	c.XORKeyStream(ks, ks) // block 0 of ChaCha20(shared, "MFE1-keys")
	st := &cipherState{}
	copy(st.rx[:], ks[:32]) // client -> relay
	copy(st.tx[:], ks[32:]) // relay -> client
	return append(append([]byte{}, kexMagic...), priv.PublicKey().Bytes()...), st, nil
}

// seal turns a plain wire frame [len][type][payload] into [len][sealed type+payload][tag]
func (s *cipherState) seal(frame []byte) []byte {
	a, _ := chacha20poly1305.New(s.tx[:])
	body := a.Seal(nil, nonceFor(s.ntx), frame[2:], nil)
	s.ntx++
	out := make([]byte, 2+len(body))
	binary.LittleEndian.PutUint16(out, uint16(len(body)))
	copy(out[2:], body)
	return out
}

// open decrypts a received frame body; returns type+payload
func (s *cipherState) open(body []byte) ([]byte, error) {
	a, _ := chacha20poly1305.New(s.rx[:])
	pt, err := a.Open(nil, nonceFor(s.nrx), body, nil)
	if err != nil {
		return nil, err
	}
	s.nrx++
	if len(pt) < 1 {
		return nil, errors.New("empty frame")
	}
	return pt, nil
}
